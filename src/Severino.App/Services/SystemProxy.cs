using System.Runtime.InteropServices;
using Microsoft.Win32;
using Severino.Core.Certificates;
using Severino.Core.Configuration;
using Severino.Core.Network;
using Severino.Core.Routes;

namespace Severino.App.Services;

/// <summary>
/// The Windows proxy of the current user, as browsers read it. With a proxy on, as on many
/// corporate VPNs, browsers would send the routes' domains to it instead of to Severino; the
/// fix is adding them to the bypass list. Never changed without the user asking.
/// </summary>
public sealed partial class SystemProxy(ConfigService config, TldDirectory tlds, string keyPath = SystemProxy.InternetSettingsKey) : IDisposable
{
    public const string InternetSettingsKey = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    private Timer? _timer;
    private ProxySettings _last = ProxySettings.None;

    /// <summary>Raised on a thread-pool thread when the settings change, e.g. a VPN connecting.</summary>
    public event EventHandler<ProxySettings>? Changed;

    public ProxySettings Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath);
        if (key is null)
            return ProxySettings.None;
        return new(
            key.GetValue("ProxyEnable") is int enabled && enabled != 0,
            key.GetValue("ProxyServer") as string,
            key.GetValue("ProxyOverride") as string,
            key.GetValue("AutoConfigURL") as string);
    }

    /// <summary>Enabled route domains a fixed proxy would take.</summary>
    public IReadOnlyList<string> Uncovered() => ProxyBypass.Uncovered(Read(), RouteRules.ActiveDomains(config.Current.Routes));

    /// <summary>Watches the settings, since a VPN can turn the proxy on at any time. Registry reads are cheap.</summary>
    public void Start()
    {
        _last = Read();
        _timer = new Timer(_ =>
        {
            var now = Read();
            if (now == _last)
                return;
            _last = now;
            Changed?.Invoke(this, now);
        }, null, PollInterval, PollInterval);
    }

    /// <summary>Adds bypass entries for every uncovered domain and remembers which ones it added.</summary>
    public async Task<IReadOnlyList<string>> AddExceptionsAsync(CancellationToken cancellationToken = default)
    {
        var uncovered = Uncovered();
        if (uncovered.Count == 0)
            return [];
        // Whole TLDs where the TLD does not exist on the internet: new .sev routes need nothing more.
        await tlds.ResolveAsync(uncovered, cancellationToken);
        var entries = ProxyBypass.EntriesFor(uncovered, tlds.Known);

        var before = Read();
        var added = entries.Where(e => !before.OverrideEntries.Contains(e, StringComparer.OrdinalIgnoreCase)).ToList();
        Write(ProxyBypass.Add(before.Override, entries));
        config.Update(c => c with
        {
            State = c.State with { ProxyBypassAdded = [.. c.State.ProxyBypassAdded.Union(added, StringComparer.OrdinalIgnoreCase)] },
        });
        return added;
    }

    /// <summary>Takes out what <see cref="AddExceptionsAsync"/> put in; entries the company set stay.</summary>
    public void RemoveAddedExceptions()
    {
        var added = config.Current.State.ProxyBypassAdded;
        if (added.Count == 0)
            return;
        Write(ProxyBypass.Remove(Read().Override, added));
        config.Update(c => c with { State = c.State with { ProxyBypassAdded = [] } });
    }

    public void Dispose() => _timer?.Dispose();

    private void Write(string bypassList)
    {
        using (var key = Registry.CurrentUser.CreateSubKey(keyPath))
            key.SetValue("ProxyOverride", bypassList, RegistryValueKind.String);
        _last = Read();
        // Tell WinINet, and through it Edge and Chrome, to reread the settings now. Tests use
        // another key and must not poke the real ones.
        if (keyPath == InternetSettingsKey)
        {
            InternetSetOption(0, InternetOptionSettingsChanged, 0, 0);
            InternetSetOption(0, InternetOptionRefresh, 0, 0);
        }
    }

    private const int InternetOptionRefresh = 37;
    private const int InternetOptionSettingsChanged = 39;

    [LibraryImport("wininet.dll", EntryPoint = "InternetSetOptionW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InternetSetOption(nint handle, int option, nint buffer, int length);
}
