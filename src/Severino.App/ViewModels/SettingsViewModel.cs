using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Severino.App.Resources;
using Severino.App.Services;
using Severino.Core.Certificates;
using Severino.Core.Configuration;
using Severino.Core.Discovery;
using Severino.Core.Routes;
using Severino.Proxy.Certificates;

namespace Severino.App.ViewModels;

public sealed record ThemeOption(AppTheme Value, string Label)
{
    // Screen readers and UI Automation read the item's ToString.
    public override string ToString() => Label;
}

public sealed record LanguageOption(AppLanguage Value, string Label)
{
    public override string ToString() => Label;
}

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ConfigService _config;
    private readonly ThemeService _themes;
    private readonly HttpsService _https;
    private readonly Backup _backup;
    private readonly AutoStart _autoStart;
    private readonly SystemCleanup _cleanup;
    private readonly Lazy<ShellService> _shell;
    private readonly Func<FirstRunViewModel> _firstRun;

    public SettingsViewModel(ConfigService config, ThemeService themes, HttpsService https, Backup backup,
        AutoStart autoStart, SystemCleanup cleanup, Lazy<ShellService> shell, Func<FirstRunViewModel> firstRun,
        WslCallers wsl, ServiceDiscovery discovery, UpdatesViewModel updates, Severino.Core.Dns.DnsSync? dnsSync = null, ProjectsViewModel? projects = null, WslTrust? trust = null)
    {
        Projects = projects;
        _trust = trust;
        Updates = updates;
        _wsl = wsl;
        _discovery = discovery;
        _dnsSync = dnsSync;
        _firstRun = firstRun;
        _config = config;
        _themes = themes;
        _https = https;
        _backup = backup;
        _autoStart = autoStart;
        _cleanup = cleanup;
        _shell = shell;

        var settings = config.Current.Settings;
        Theme = settings.Theme;
        Language = settings.Language;
        StartWithWindows = autoStart.IsEnabled;
        StartMinimized = settings.StartMinimized;
        HealthNotifications = settings.HealthNotifications;
        WatchServices = settings.WatchServices;
        HttpPort = settings.HttpPort.ToString();
        HttpsPort = settings.HttpsPort.ToString();

        RefreshHttps();
        https.Changed += (_, _) => Dispatch(RefreshHttps);
        config.Changed += (_, _) => Dispatch(RefreshHttps);
        InitializeWsl();
    }

    public IReadOnlyList<ThemeOption> Themes { get; } =
    [
        new(AppTheme.Auto, Strings.Common_SameAsWindows),
        new(AppTheme.Light, Strings.Settings_ThemeLight),
        new(AppTheme.Dark, Strings.Settings_ThemeDark),
    ];

    public string ConfigDirectory => _config.Directory;

    /// <summary>Settings › Sobre: the installed version, the last check and the update itself.</summary>
    public UpdatesViewModel Updates { get; }

    [ObservableProperty]
    public partial AppTheme Theme { get; set; }

    [ObservableProperty]
    public partial bool StartMinimized { get; set; }

    partial void OnThemeChanged(AppTheme value)
    {
        // Runs once from the constructor too; Update skips the write when nothing changed.
        _config.Update(c => c with { Settings = c.Settings with { Theme = value } });
        _themes.Apply(value);
    }

    /// <summary>Each language by its own name, so someone lost in the other one still finds theirs.</summary>
    public IReadOnlyList<LanguageOption> Languages { get; } =
    [
        new(AppLanguage.Auto, Strings.Common_SameAsWindows),
        new(AppLanguage.English, "English"),
        new(AppLanguage.Portuguese, "Português (Brasil)"),
    ];

    [ObservableProperty]
    public partial AppLanguage Language { get; set; }

    /// <summary>The chosen language differs from the one on screen: it takes a restart.</summary>
    [ObservableProperty]
    public partial bool LanguageNeedsRestart { get; set; }

    partial void OnLanguageChanged(AppLanguage value)
    {
        _config.Update(c => c with { Settings = c.Settings with { Language = value } });
        LanguageNeedsRestart = UiLanguage.CultureFor(value, UiLanguage.Windows).Name != CultureInfo.CurrentUICulture.Name;
    }

    [RelayCommand]
    private Task RestartAsync() => _shell.Value.RestartAsync();

    partial void OnStartMinimizedChanged(bool value) =>
        _config.Update(c => c with { Settings = c.Settings with { StartMinimized = value } });

    /// <summary>Settings › Projetos; null where the window is built without one, as in tests.</summary>
    public ProjectsViewModel? Projects { get; }

    /// <summary>The tray notices of routes and services that asked for them.</summary>
    [ObservableProperty]
    public partial bool HealthNotifications { get; set; }

    partial void OnHealthNotificationsChanged(bool value) =>
        _config.Update(c => c with { Settings = c.Settings with { HealthNotifications = value } });

    /// <summary>Follow imported services on their own: Docker events, Kubernetes every 30 s.</summary>
    [ObservableProperty]
    public partial bool WatchServices { get; set; }

    partial void OnWatchServicesChanged(bool value) =>
        _config.Update(c => c with { Settings = c.Settings with { WatchServices = value } });

    /// <summary>The Run key is the truth; the config mirrors it for exports and support.</summary>
    [ObservableProperty]
    public partial bool StartWithWindows { get; set; }

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (value == _autoStart.IsEnabled)
            return; // the constructor reading the current state
        if (value)
            _autoStart.Enable();
        else
            _autoStart.Disable();
        _config.Update(c => c with { Settings = c.Settings with { StartWithWindows = value } });
    }

    [ObservableProperty]
    public partial string HttpPort { get; set; }

    [ObservableProperty]
    public partial string? HttpPortError { get; set; }

    partial void OnHttpPortChanged(string value) => HttpPortError = null;

    /// <summary>Saves the port; the proxy moves to it right away.</summary>
    [RelayCommand]
    private void ApplyHttpPort()
    {
        HttpPortError = ParsePort(HttpPort, _config.Current.Settings.HttpsPort, out var port);
        if (HttpPortError is not null)
            return;
        HttpPort = port.ToString();
        _config.Update(c => c with { Settings = c.Settings with { HttpPort = port } });
    }

    [ObservableProperty]
    public partial string HttpsPort { get; set; }

    [ObservableProperty]
    public partial string? HttpsPortError { get; set; }

    partial void OnHttpsPortChanged(string value) => HttpsPortError = null;

    [RelayCommand]
    private void ApplyHttpsPort()
    {
        HttpsPortError = ParsePort(HttpsPort, _config.Current.Settings.HttpPort, out var port);
        if (HttpsPortError is not null)
            return;
        HttpsPort = port.ToString();
        _config.Update(c => c with { Settings = c.Settings with { HttpsPort = port } });
    }

    private static string? ParsePort(string? text, int otherPort, out int port)
    {
        if (!int.TryParse(text?.Trim(), out port) || port is < 1 or > 65535)
            return Strings.Settings_PortRange;
        if (port == otherPort)
            return Strings.Settings_PortsDiffer;
        return null;
    }

    // HTTPS

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ActivateHttpsCommand), nameof(ReissueHttpsCommand), nameof(RemoveHttpsCommand), nameof(ExportCaCommand))]
    public partial bool HttpsActive { get; set; }

    /// <summary>The CA exists but cannot be used: untrusted by Windows or unreadable.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ActivateHttpsCommand), nameof(RemoveHttpsCommand))]
    public partial bool HttpsBroken { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ActivateHttpsCommand))]
    public partial bool HasRoutes { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ActivateHttpsCommand), nameof(ReissueHttpsCommand), nameof(RemoveHttpsCommand), nameof(ExportCaCommand))]
    public partial bool HttpsBusy { get; set; }

    [ObservableProperty]
    public partial string HttpsStateText { get; set; } = "";

    /// <summary>"Cobre: .sev, api.empresa.com"; empty while inactive.</summary>
    [ObservableProperty]
    public partial string CoverageText { get; set; } = "";

    /// <summary>HTTPS routes the CA does not cover yet; reissuing fixes it.</summary>
    [ObservableProperty]
    public partial string? UncoveredText { get; set; }

    /// <summary>The outcome of the last action, shown until the next one.</summary>
    [ObservableProperty]
    public partial string? HttpsMessage { get; set; }

    /// <summary>The command that makes Node trust the exported CA.</summary>
    [ObservableProperty]
    public partial string? NodeCommand { get; set; }

    /// <summary>Firefox profiles that keep their own CA list and would refuse the local CA; null when none.</summary>
    [ObservableProperty]
    public partial string? FirefoxHint { get; set; }

    /// <summary>Why Strings.Settings_HttpsActivate is disabled, when it is because there are no routes.</summary>
    public string? ActivateHint => HasRoutes || HttpsActive ? null : Strings.Settings_ActivateHint;

    partial void OnHasRoutesChanged(bool value) => OnPropertyChanged(nameof(ActivateHint));
    partial void OnHttpsActiveChanged(bool value) => OnPropertyChanged(nameof(ActivateHint));

    private bool CanActivate() => HasRoutes && !HttpsActive && !HttpsBusy;
    private bool CanUseCa() => HttpsActive && !HttpsBusy;
    private bool CanRemove() => (HttpsActive || HttpsBroken) && !HttpsBusy;

    [RelayCommand(CanExecute = nameof(CanActivate))]
    private Task ActivateHttpsAsync() => RunAsync(async () => HttpsMessage = Describe(
        await _https.ActivateAsync(prompt => DialogService.ConfirmTrustAsync(Strings.Settings_HttpsActivate, prompt)),
        done: Strings.Settings_HttpsOn));

    [RelayCommand(CanExecute = nameof(CanUseCa))]
    private Task ReissueHttpsAsync() => RunAsync(async () => HttpsMessage = Describe(
        await _https.ReissueAsync(prompt => DialogService.ConfirmTrustAsync(Strings.Settings_ReissueTitle, prompt)),
        done: Strings.Settings_Reissued));

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private Task RemoveHttpsAsync() => RunAsync(async () =>
    {
        if (!await DialogService.ConfirmAsync(Strings.Settings_RemoveCa,
                Strings.Settings_RemoveCaConfirm,
                Strings.Common_Remove))
            return;
        NodeCommand = null;
        HttpsMessage = await _https.RemoveAsync()
            ? Strings.Settings_CaRemoved
            : Strings.Settings_CaRemovedRootKept;
    });

    [RelayCommand(CanExecute = nameof(CanUseCa))]
    private void ExportCa()
    {
        var path = DialogService.PickSavePath("severino-ca.pem", Strings.Settings_PemFilter);
        if (path is null)
            return;
        try
        {
            _https.ExportPem(path);
            NodeCommand = $"setx NODE_EXTRA_CA_CERTS \"{path}\"";
            HttpsMessage = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            HttpsMessage = string.Format(Strings.Common_CouldNotWriteFile, ex.Message);
        }
    }

    [RelayCommand]
    private void CopyNodeCommand()
    {
        if (NodeCommand is not null)
            Clipboard.SetText(NodeCommand);
    }

    private async Task RunAsync(Func<Task> action)
    {
        HttpsBusy = true;
        HttpsMessage = null;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            HttpsMessage = string.Format(Strings.Common_SomethingWentWrong, ex.Message);
        }
        finally
        {
            HttpsBusy = false;
        }
    }

    private static string? Describe(HttpsActionResult result, string done) => result switch
    {
        HttpsActionResult.Done => done,
        HttpsActionResult.DoneOldRootKept => done + Strings.Settings_OldRootKept,
        HttpsActionResult.Declined => Strings.Settings_CaDeclined,
        HttpsActionResult.Cancelled => null,
        _ => Strings.Settings_CreateRouteFirst,
    };

    private void RefreshHttps()
    {
        var status = _https.Status;
        HasRoutes = _config.Current.Routes.Count > 0;
        HttpsActive = status.State == LocalCaState.Active;
        HttpsBroken = status.State is LocalCaState.NotTrusted or LocalCaState.Unreadable;
        HttpsStateText = status.State switch
        {
            LocalCaState.Active => string.Format(Strings.Settings_HttpsActiveUntil, status.NotAfter?.LocalDateTime.ToString("d", CultureInfo.CurrentCulture)),
            LocalCaState.NotTrusted => Strings.Settings_CaNotTrusted,
            LocalCaState.Unreadable => Strings.Settings_CaUnreadable,
            _ => Strings.Settings_HttpsOff,
        };
        var firefox = HttpsActive ? FirefoxTrust.ProfilesIgnoringWindowsRoots() : [];
        FirefoxHint = firefox.Count == 0 ? null
            : string.Format(Strings.Settings_FirefoxHint, firefox.Count == 1 ? Strings.Settings_FirefoxProfile : Strings.Settings_FirefoxProfiles,
                string.Join(", ", firefox), FirefoxTrust.Preference);
        CoverageText = HttpsActive
            ? string.Format(Strings.Settings_Covers, HttpsService.DescribeNames(status.Names))
            : "";
        var uncovered = _https.Uncovered();
        UncoveredText = uncovered.Count switch
        {
            0 => null,
            1 => string.Format(Strings.Settings_UncoveredOne, uncovered[0]),
            _ => string.Format(Strings.Settings_UncoveredMany, string.Join(", ", uncovered)),
        };
    }

    private static void Dispatch(Action action) => Application.Current?.Dispatcher.BeginInvoke(action);

    [RelayCommand]
    private async Task ExportBackupAsync()
    {
        var path = DialogService.PickSavePath("severino-backup.json", Strings.Settings_BackupFilter);
        if (path is null)
            return;
        try
        {
            File.WriteAllText(path, _backup.Export());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await DialogService.ShowInfoAsync(Strings.Settings_ExportBackup, string.Format(Strings.Common_CouldNotWriteFile, ex.Message));
        }
    }

    [RelayCommand]
    private async Task ImportBackupAsync()
    {
        var path = DialogService.PickOpenPath(Strings.Settings_BackupOpenFilter);
        if (path is null)
            return;

        BackupResult result;
        try
        {
            result = _backup.Import(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidRouteFileException)
        {
            await DialogService.ShowInfoAsync(Strings.Settings_ImportBackup, string.Format(Strings.Settings_ImportFailed, ex.Message));
            return;
        }

        await DialogService.ShowInfoAsync(Strings.Settings_ImportBackup, DescribeImport(result));
        await OfferReissueAsync(result.Routes.Added);
    }

    /// <summary>The summary after an import: what came in, what was already here, what was refused.</summary>
    public static string DescribeImport(BackupResult result)
    {
        var routes = result.Routes;
        var came = new[]
        {
            Count(routes.Added.Count, Strings.Count_Route, Strings.Count_Routes),
            Count(result.Services.Count, Strings.Count_Service, Strings.Count_Services),
            Count(result.Dns.Count, Strings.Count_DnsEntry, Strings.Count_DnsEntries),
        }.OfType<string>().ToList();
        var lines = new List<string>
        {
            came.Count == 0 ? Strings.Settings_ImportNothing : string.Format(Strings.Settings_Imported, string.Join(", ", came)),
        };
        if (routes.Skipped.Count > 0)
            lines.Add(string.Format(Strings.Settings_ImportSkipped, string.Join(", ", routes.Skipped)));
        var refused = routes.Invalid.Concat(result.Refused).ToList();
        if (refused.Count > 0)
            lines.Add(Strings.Settings_ImportRefused + "\n" + string.Join("\n", refused.Select(i => $"• {i.Domain}: {i.Reason}")));
        return string.Join("\n\n", lines);

        static string? Count(int n, string one, string many) => n switch { 0 => null, 1 => one, _ => string.Format(many, n) };
    }

    // Like saving a route in the form: imported HTTPS routes outside the CA need a new one.
    private async Task OfferReissueAsync(IReadOnlyList<RouteEntry> added)
    {
        var uncovered = added.Where(r => r.Https && _https.IsActive && !_https.Covers(r.Domain)).Select(r => r.Domain).ToList();
        if (uncovered.Count == 0)
            return;
        var lead = string.Format(Strings.Settings_ImportReissueLead, string.Join(", ", uncovered));
        await RunAsync(async () => HttpsMessage = Describe(
            await _https.ReissueAsync(prompt => DialogService.ConfirmTrustAsync(Strings.Settings_ReissueTitle, prompt, lead)),
            done: Strings.Settings_ReissuedForImport));
    }

    [RelayCommand]
    private async Task CleanAllAsync()
    {
        var (confirmed, deleteData) = await DialogService.ConfirmCleanupAsync();
        if (!confirmed)
            return;

        var result = _cleanup.Run();
        if (!result.CaRemoved)
            await DialogService.ShowInfoAsync(Strings.Settings_CleanUp,
                Strings.Settings_CleanUpRootKept);

        // The DNS block outlives an exit, so it goes now; lines outside Severino stay as they are.
        if (_dnsSync is not null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try
            {
                await _dnsSync.ClearAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
            }
        }

        // The CA in the WSL distros outlives an exit too.
        if (_trust is not null)
            await _trust.RemoveEverywhereAsync(TimeSpan.FromSeconds(30));

        var shell = _shell.Value;
        shell.DeleteDataOnExit = deleteData;
        // Exiting clears the hosts block, like any exit.
        await shell.ExitAsync();
    }

    [RelayCommand]
    private void ReviewSetup() => DialogService.ShowFirstRun(_firstRun());

    [RelayCommand]
    private void OpenConfigFolder()
    {
        Directory.CreateDirectory(ConfigDirectory);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{ConfigDirectory}\"") { UseShellExecute = true });
    }
}
