using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using Severino.App.Services;
using Severino.Proxy.Certificates;
using Severino.Tests.Certificates;

namespace Severino.Tests.App;

/// <summary>Autostart and cleanup, against a throwaway registry key: never the real Run key.</summary>
public sealed class SystemCleanupTests : IDisposable
{
    private readonly string _keyPath = $@"Software\Severino\Tests\{Guid.NewGuid():N}\Run";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "severino-tests", Guid.NewGuid().ToString("N"));
    private readonly string _exe;

    public SystemCleanupTests()
    {
        Directory.CreateDirectory(_dir);
        _exe = Path.Combine(_dir, "Severino.exe");
        File.WriteAllText(_exe, "");
    }

    public void Dispose()
    {
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
    public void Cleanup_removes_the_ca_its_files_and_the_autostart()
    {
        var trust = new FakeTrustStore();
        var store = new CaStore(Path.Combine(_dir, "ca"));
        var ca = new LocalCa(store, trust, TimeProvider.System, NullLogger<LocalCa>.Instance);
        ca.Activate(["sev"]);
        var autoStart = new AutoStart(_keyPath, _exe);
        autoStart.Enable();

        var result = new SystemCleanup(ca, autoStart).Run();

        Assert.True(result.CaRemoved);
        Assert.Empty(trust.Roots);
        Assert.False(store.Exists);
        Assert.False(autoStart.IsEnabled);
    }

    [Fact]
    public void Cleanup_reports_a_root_the_user_kept()
    {
        var trust = new FakeTrustStore();
        var ca = new LocalCa(new CaStore(Path.Combine(_dir, "ca")), trust, TimeProvider.System, NullLogger<LocalCa>.Instance);
        ca.Activate(["sev"]);
        trust.DeclineRemoval = true;

        Assert.False(new SystemCleanup(ca, new AutoStart(_keyPath, _exe)).Run().CaRemoved);
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
