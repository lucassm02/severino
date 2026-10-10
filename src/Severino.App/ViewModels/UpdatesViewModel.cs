using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Severino.App.Services;
using Severino.App.Tray;
using Severino.Core.Configuration;
using Severino.Core.Updates;

namespace Severino.App.ViewModels;

/// <summary>
/// New versions: asks GitHub 30 s after start and then once a day, shows the notice at the top
/// of the window and in Settings › Sobre, and installs one by running its verified setup.
/// </summary>
public sealed partial class UpdatesViewModel : ObservableObject
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    private readonly ConfigService _config;
    private readonly TrayService _tray;
    private readonly Lazy<ShellService> _shell;
    private readonly ILogger<UpdatesViewModel> _logger;
    private readonly UpdateChecker _checker;
    private readonly UpdateDownloader _downloader = new(Http);

    public UpdatesViewModel(ConfigService config, TrayService tray, Lazy<ShellService> shell, ILogger<UpdatesViewModel> logger)
    {
        _config = config;
        _tray = tray;
        _shell = shell;
        _logger = logger;
        _checker = new UpdateChecker(Http, typeof(UpdatesViewModel).Assembly.GetName().Version ?? new Version(0, 0, 0));
        AutoCheck = config.Current.Settings.CheckForUpdates;
        UpdateLastCheckText();
    }

    public string CurrentVersion => _checker.Current.ToString(3);

    /// <summary>A newer release the person has not skipped; null when there is none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdate), nameof(BannerTitle), nameof(InstallText))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand), nameof(SkipCommand), nameof(OpenReleasePageCommand))]
    public partial ReleaseInfo? Available { get; set; }

    public bool HasUpdate => Available is not null;

    /// <summary>The notice at the top of the window.</summary>
    [ObservableProperty]
    public partial bool IsBannerOpen { get; set; }

    public string BannerTitle => Available is { } r ? $"Severino {r.Version.ToString(3)} saiu" : "";

    /// <summary>"Atualizar agora", or, without a setup GitHub can vouch for, the release page.</summary>
    public string InstallText => Available?.Installer?.Sha256 is null ? "Baixar no GitHub" : "Atualizar agora";

    /// <summary>Under the title: the installed version, the download, or what went wrong.</summary>
    [ObservableProperty]
    public partial string BannerMessage { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand), nameof(CheckNowCommand), nameof(SkipCommand))]
    public partial bool IsDownloading { get; set; }

    /// <summary>From 0 to 100.</summary>
    [ObservableProperty]
    public partial double DownloadProgress { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckNowCommand))]
    public partial bool IsChecking { get; set; }

    /// <summary>Settings › Sobre: the result of the last check.</summary>
    [ObservableProperty]
    public partial string StatusText { get; set; } = "";

    [ObservableProperty]
    public partial string LastCheckText { get; set; } = "";

    [ObservableProperty]
    public partial bool AutoCheck { get; set; }

    partial void OnAutoCheckChanged(bool value) =>
        _config.Update(c => c with { Settings = c.Settings with { CheckForUpdates = value } });

    /// <summary>The first check 30 s after start, then a look every hour for a day-old answer.</summary>
    public void Start() => _ = RunAsync();

    private async Task RunAsync()
    {
#if DEBUG
        // A dev build is always "old" next to the releases; Procurar agora still works.
        await Task.CompletedTask;
#else
        await Task.Delay(TimeSpan.FromSeconds(30));
        // Always once per start: what GitHub said before is not kept, only when it said it.
        if (AutoCheck)
            await CheckAsync(manual: false);
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        while (await timer.WaitForNextTickAsync())
        {
            if (AutoCheck && UpdatePolicy.IsDue(_config.Current.State.LastUpdateCheck, DateTimeOffset.Now))
                await CheckAsync(manual: false);
        }
#endif
    }

    private bool CanCheck() => !IsChecking && !IsDownloading;

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private Task CheckNowAsync() => CheckAsync(manual: true);

    private async Task CheckAsync(bool manual)
    {
        if (IsChecking || IsDownloading)
            return;
        IsChecking = true;
        StatusText = "Consultando o GitHub…";
        try
        {
            var latest = await _checker.GetLatestAsync();
            _config.Update(c => c with { State = c.State with { LastUpdateCheck = DateTimeOffset.Now } });
            UpdateLastCheckText();
            Show(latest, manual);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UpdateCheckException or System.Text.Json.JsonException)
        {
            _logger.LogInformation("Update check failed: {Error}", ex.Message);
            // Offline is normal: only someone who pressed the button hears about it.
            StatusText = manual ? $"Não deu para consultar o GitHub: {ex.Message}" : Describe(Available);
        }
        finally
        {
            IsChecking = false;
        }
    }

    private void Show(ReleaseInfo latest, bool manual)
    {
        var newer = _checker.IsNewer(latest);
        // Someone who asks sees a skipped version again; the automatic check respects the skip.
        Available = newer && (manual || UpdatePolicy.ShouldOffer(latest, _checker.Current, _config.Current.State.SkippedVersion))
            ? latest
            : null;
        StatusText = Describe(Available);
        if (Available is not { } release)
            return;

        BannerMessage = $"Você está na {CurrentVersion}. A atualização mantém rotas, serviços e configurações.";
        IsBannerOpen = true;

        var version = release.Version.ToString(3);
        if (_config.Current.State.NotifiedVersion == version)
            return;
        // The window shows the notice already; the tray speaks only when it is hidden.
        if (Application.Current?.MainWindow is not { IsVisible: true })
            _tray.ShowInfo($"Severino {version} saiu", "Abra o Severino e clique em Atualizar agora.");
        _config.Update(c => c with { State = c.State with { NotifiedVersion = version } });
    }

    private string Describe(ReleaseInfo? available) => available is { } r
        ? $"A versão {r.Version.ToString(3)} saiu. Você está na {CurrentVersion}."
        : _config.Current.State.LastUpdateCheck is null ? "" : "Você está na versão mais recente.";

    private void UpdateLastCheckText()
    {
        if (_config.Current.State.LastUpdateCheck is not { } last)
        {
            LastCheckText = "";
            return;
        }
        var local = last.ToLocalTime();
        var day = local.Date == DateTime.Today ? "hoje"
            : local.Date == DateTime.Today.AddDays(-1) ? "ontem"
            : local.ToString("d", CultureInfo.CurrentCulture);
        LastCheckText = $"Última consulta: {day}, às {local:HH:mm}";
    }

    private bool HasUpdateIdle() => Available is not null && !IsDownloading;

    [RelayCommand(CanExecute = nameof(HasUpdateIdle))]
    private async Task InstallAsync()
    {
        if (Available is not { } release)
            return;
        if (release.Installer is not { Sha256: not null } installer)
        {
            OpenReleasePage();
            return;
        }

        var version = release.Version.ToString(3);
        var size = installer.Size > 0 ? $" ({installer.Size / (1024 * 1024)} MB)" : "";
        if (!await DialogService.ConfirmAsync("Atualizar o Severino",
                $"O Severino baixa a versão {version}{size}, fecha, instala e abre de novo. O Windows vai pedir permissão de administrador. " +
                "Rotas, serviços e configurações ficam como estão.",
                "Atualizar"))
            return;

        IsBannerOpen = true;
        IsDownloading = true;
        DownloadProgress = 0;
        BannerMessage = $"Baixando a versão {version}…";
        string setup;
        try
        {
            setup = await _downloader.DownloadAsync(installer, UpdateDownloader.DefaultDirectory,
                new Progress<double>(p => DownloadProgress = p * 100));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UpdateDownloadException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Update download failed");
            BannerMessage = ex is UpdateDownloadException ? ex.Message : $"O download falhou: {ex.Message}";
            return;
        }
        finally
        {
            IsDownloading = false;
        }

        try
        {
            // The setup asks for the UAC, closes nothing (the app is gone by then) and, with
            // RELAUNCH, opens the new version as this user when it is done.
            Process.Start(new ProcessStartInfo(setup, "/SILENT /SP- /NORESTART /RELAUNCH=1") { UseShellExecute = true });
        }
        catch (Win32Exception ex)
        {
            _logger.LogWarning(ex, "Could not start the setup");
            BannerMessage = $"Não deu para abrir o instalador: {ex.Message}";
            return;
        }
        _logger.LogInformation("Installing {Version}; exiting", version);
        await _shell.Value.ExitAsync();
    }

    [RelayCommand(CanExecute = nameof(HasUpdate))]
    private void OpenReleasePage()
    {
        if (Available is { } release)
            Process.Start(new ProcessStartInfo(release.Page.AbsoluteUri) { UseShellExecute = true });
    }

    /// <summary>Hides the notice until the app starts again; Settings › Sobre still offers the update.</summary>
    [RelayCommand]
    private void Dismiss() => IsBannerOpen = false;

    /// <summary>No notice for this version again; a newer one brings it back.</summary>
    [RelayCommand(CanExecute = nameof(HasUpdateIdle))]
    private void Skip()
    {
        if (Available is not { } release)
            return;
        _config.Update(c => c with { State = c.State with { SkippedVersion = release.Version.ToString(3) } });
        Available = null;
        IsBannerOpen = false;
        StatusText = $"A versão {release.Version.ToString(3)} foi pulada. Procurar agora mostra ela de novo.";
    }
}
