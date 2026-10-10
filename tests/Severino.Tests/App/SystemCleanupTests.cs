using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using Severino.App.Services;
using Severino.Core.Certificates;
using Severino.Core.Configuration;
using Severino.Core.Domains;
using Severino.Core.Network;
using Severino.Proxy.Certificates;
using Severino.Tests.Certificates;

namespace Severino.Tests.App;

/// <summary>Autostart and cleanup, against a throwaway registry key: never the real Run key.</summary>
public sealed class SystemCleanupTests : IDisposable
{
    private readonly string _keyPath = $@"Software\Severino\Tests\{Guid.NewGuid():N}\Run";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "severino-tests", Guid.NewGuid().ToString("N"));
    private readonly string _exe;
    private readonly ConfigService _config;
    private readonly SystemProxy _proxy;

    public SystemCleanupTests()
    {
        Directory.CreateDirectory(_dir);
        _exe = Path.Combine(_dir, "Severino.exe");
        File.WriteAllText(_exe, "");
        _config = new ConfigService(new ConfigStore(Path.Combine(_dir, "config")));
        _config.Load();
        _proxy = new SystemProxy(_config, new TldDirectory(_config, new FakeDns()), ProxyKey);
    }

    /// <summary>A stand-in for HKCU Internet Settings, next to the test Run key.</summary>
    private string ProxyKey => Path.Combine(Path.GetDirectoryName(_keyPath)!, "Internet Settings");

    private void SetProxy(bool enabled, string? server, string? bypass)
    {
        using var key = Registry.CurrentUser.CreateSubKey(ProxyKey);
        key.SetValue("ProxyEnable", enabled ? 1 : 0, RegistryValueKind.DWord);
        if (server is not null)
            key.SetValue("ProxyServer", server);
        if (bypass is not null)
            key.SetValue("ProxyOverride", bypass);
    }

    private void SetRoutes(params string[] domains) => _config.Update(c => c with
    {
        Routes = [.. domains.Select(d => new RouteEntry { Domain = d, Target = "http://localhost:3000" })],
    });

    public void Dispose()
    {
        _proxy.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(Path.GetDirectoryName(_keyPath)!, throwOnMissingSubKey: false);
        // Leave no empty parents behind either.
        foreach (var parent in new[] { @"Software\Severino\Tests", @"Software\Severino" })
        {
            using var key = Registry.CurrentUser.OpenSubKey(parent);
            if (key is { SubKeyCount: 0, ValueCount: 0 })
            {
                key.Dispose();
                Registry.CurrentUser.DeleteSubKey(parent, throwOnMissingSubKey: false);
            }
        }
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private string? RunValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_keyPath);
        return key?.GetValue(AutoStart.ValueName) as string;
    }

    [Fact]
    public void Autostart_writes_and_removes_the_run_entry()
    {
        var autoStart = new AutoStart(_keyPath, _exe);

        autoStart.Enable();
        Assert.True(autoStart.IsEnabled);
        Assert.Equal($"\"{_exe}\" --autostart", RunValue());

        autoStart.Disable();
        Assert.False(autoStart.IsEnabled);
        Assert.Null(RunValue());
        autoStart.Disable(); // already gone: no error
    }

    [Fact]
    public void Repair_fixes_only_an_entry_whose_executable_is_gone()
    {
        var other = Path.Combine(_dir, "Instalado.exe");
        File.WriteAllText(other, "");
        new AutoStart(_keyPath, other).Enable();
        var mine = new AutoStart(_keyPath, _exe);

        mine.Repair();
        Assert.Equal($"\"{other}\" --autostart", RunValue()); // a working entry is left alone

        File.Delete(other);
        mine.Repair();
        Assert.Equal(mine.Command, RunValue());
    }

    [Fact]
    public void Repair_never_creates_an_entry()
    {
        new AutoStart(_keyPath, _exe).Repair();

        Assert.Null(RunValue());
    }

    [Fact]
    public async Task Cleanup_removes_the_ca_its_files_the_autostart_and_the_proxy_exceptions()
    {
        var trust = new FakeTrustStore();
        var store = new CaStore(Path.Combine(_dir, "ca"));
        var ca = new LocalCa(store, trust, TimeProvider.System, NullLogger<LocalCa>.Instance);
        ca.Activate(["sev"]);
        var autoStart = new AutoStart(_keyPath, _exe);
        autoStart.Enable();

        SetProxy(true, "proxy.empresa:8080", "<local>");
        SetRoutes("callfred.sev");
        await _proxy.AddExceptionsAsync();

        var result = new SystemCleanup(ca, autoStart, _proxy).Run();

        Assert.True(result.CaRemoved);
        Assert.Empty(trust.Roots);
        Assert.False(store.Exists);
        Assert.False(autoStart.IsEnabled);
        Assert.Equal("<local>", _proxy.Read().Override);
    }

    [Fact]
    public void Cleanup_reports_a_root_the_user_kept()
    {
        var trust = new FakeTrustStore();
        var ca = new LocalCa(new CaStore(Path.Combine(_dir, "ca")), trust, TimeProvider.System, NullLogger<LocalCa>.Instance);
        ca.Activate(["sev"]);
        trust.DeclineRemoval = true;

        Assert.False(new SystemCleanup(ca, new AutoStart(_keyPath, _exe), _proxy).Run().CaRemoved);
    }

    [Fact]
    public void Reads_the_proxy_settings()
    {
        Assert.Equal(ProxySettings.None, _proxy.Read());

        SetProxy(false, "127.0.0.1:5559", null);
        Assert.False(_proxy.Read().HasFixedProxy);

        SetProxy(true, "proxy.empresa:8080", "<local>;10.*");
        Assert.Equal(new ProxySettings(true, "proxy.empresa:8080", "<local>;10.*", null), _proxy.Read());
    }

    [Fact]
    public async Task Exceptions_are_added_once_and_removed_without_touching_the_company_ones()
    {
        SetProxy(true, "proxy.empresa:8080", "<local>;*.empresa.local");
        SetRoutes("callfred.sev", "api.sev", "api.empresa.com");
        Assert.Equal(3, _proxy.Uncovered().Count);

        var added = await _proxy.AddExceptionsAsync();

        Assert.Equal(["*.sev", "api.empresa.com"], added.Order(StringComparer.Ordinal));
        Assert.Empty(_proxy.Uncovered());
        Assert.Equal("<local>;*.empresa.local;api.empresa.com;*.sev", _proxy.Read().Override);
        Assert.Empty(await _proxy.AddExceptionsAsync()); // nothing left to add

        _proxy.RemoveAddedExceptions();
        Assert.Equal("<local>;*.empresa.local", _proxy.Read().Override);
        Assert.Empty(_config.Current.State.ProxyBypassAdded);
    }

    [Fact]
    public async Task An_entry_the_company_already_had_is_not_claimed()
    {
        SetProxy(true, "proxy.empresa:8080", "*.sev");
        SetRoutes("callfred.sev", "api.empresa.com");

        await _proxy.AddExceptionsAsync();
        _proxy.RemoveAddedExceptions();

        Assert.Equal("*.sev", _proxy.Read().Override);
    }

    /// <summary>"sev" does not exist on the internet, "com" does.</summary>
    private sealed class FakeDns : IDnsResolver
    {
        public Task<DnsLookupResult> LookupAsync(string name, CancellationToken cancellationToken) =>
            Task.FromResult(new DnsLookupResult(DnsLookupOutcome.NotFound));

        public Task<bool?> TldExistsAsync(string tld, CancellationToken cancellationToken) =>
            Task.FromResult<bool?>(tld != "sev");
    }

    [Fact]
    public void Delete_data_removes_the_whole_folder()
    {
        var data = Path.Combine(_dir, "data");
        Directory.CreateDirectory(Path.Combine(data, "backups"));
        File.WriteAllText(Path.Combine(data, "config.json"), "{}");
        File.WriteAllText(Path.Combine(data, "backups", "config.1.json"), "{}");

        SystemCleanup.DeleteData(data);

        Assert.False(Directory.Exists(data));
        SystemCleanup.DeleteData(data); // already gone: no error
    }
}
