using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Severino.App.Services;
using Severino.Core.Certificates;
using Severino.Core.Configuration;
using Severino.Core.Routes;
using Severino.Proxy.Certificates;

namespace Severino.App.ViewModels;

public sealed record ThemeOption(AppTheme Value, string Label)
{
    // Screen readers and UI Automation read the item's ToString.
    public override string ToString() => Label;
}

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ConfigService _config;
    private readonly ThemeService _themes;
    private readonly HttpsService _https;
    private readonly RouteService _routes;
    private readonly AutoStart _autoStart;
    private readonly SystemCleanup _cleanup;
    private readonly Lazy<ShellService> _shell;
    private readonly Func<FirstRunViewModel> _firstRun;

    public SettingsViewModel(ConfigService config, ThemeService themes, HttpsService https, RouteService routes,
        AutoStart autoStart, SystemCleanup cleanup, Lazy<ShellService> shell, Func<FirstRunViewModel> firstRun)
    {
        _firstRun = firstRun;
        _config = config;
        _themes = themes;
        _https = https;
        _routes = routes;
        _autoStart = autoStart;
        _cleanup = cleanup;
        _shell = shell;

        var settings = config.Current.Settings;
        Theme = settings.Theme;
        StartWithWindows = autoStart.IsEnabled;
        StartMinimized = settings.StartMinimized;
        HttpPort = settings.HttpPort.ToString();
        HttpsPort = settings.HttpsPort.ToString();

        RefreshHttps();
        https.Changed += (_, _) => Dispatch(RefreshHttps);
        config.Changed += (_, _) => Dispatch(RefreshHttps);
    }

    public IReadOnlyList<ThemeOption> Themes { get; } =
    [
        new(AppTheme.Auto, "Igual ao Windows"),
        new(AppTheme.Light, "Claro"),
        new(AppTheme.Dark, "Escuro"),
    ];

    public string ConfigDirectory => _config.Directory;

    public string Version { get; } =
        typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "";

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

    partial void OnStartMinimizedChanged(bool value) =>
        _config.Update(c => c with { Settings = c.Settings with { StartMinimized = value } });

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
            return "Use um número de 1 a 65535.";
        if (port == otherPort)
            return "HTTP e HTTPS precisam de portas diferentes.";
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

    /// <summary>Why "Ativar HTTPS" is disabled, when it is because there are no routes.</summary>
    public string? ActivateHint => HasRoutes || HttpsActive ? null : "Crie uma rota primeiro: a CA só vale para os domínios cadastrados.";

    partial void OnHasRoutesChanged(bool value) => OnPropertyChanged(nameof(ActivateHint));
    partial void OnHttpsActiveChanged(bool value) => OnPropertyChanged(nameof(ActivateHint));

    private bool CanActivate() => HasRoutes && !HttpsActive && !HttpsBusy;
    private bool CanUseCa() => HttpsActive && !HttpsBusy;
    private bool CanRemove() => (HttpsActive || HttpsBroken) && !HttpsBusy;

    [RelayCommand(CanExecute = nameof(CanActivate))]
    private Task ActivateHttpsAsync() => RunAsync(async () => HttpsMessage = Describe(
        await _https.ActivateAsync(prompt => DialogService.ConfirmTrustAsync("Ativar HTTPS", prompt)),
        done: "HTTPS ativo. As rotas abrem com https://, e o http:// continua funcionando."));

    [RelayCommand(CanExecute = nameof(CanUseCa))]
    private Task ReissueHttpsAsync() => RunAsync(async () => HttpsMessage = Describe(
        await _https.ReissueAsync(prompt => DialogService.ConfirmTrustAsync("Reemitir a CA", prompt)),
        done: "CA reemitida. Os certificados das rotas são refeitos no próximo acesso."));

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private Task RemoveHttpsAsync() => RunAsync(async () =>
    {
        if (!await DialogService.ConfirmAsync("Remover CA",
                "O HTTPS para de responder e as chaves da CA são apagadas. O Windows pede confirmação para tirar a CA da lista de confiáveis.",
                "Remover"))
            return;
        NodeCommand = null;
        HttpsMessage = await _https.RemoveAsync()
            ? "CA removida."
            : "As chaves foram apagadas, mas a CA ficou na lista de confiáveis do Windows porque o pedido foi recusado. Sem a chave, ela não assina mais nada.";
    });

    [RelayCommand(CanExecute = nameof(CanUseCa))]
    private void ExportCa()
    {
        var path = DialogService.PickSavePath("severino-ca.pem", "Certificado PEM (*.pem)|*.pem");
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
            HttpsMessage = $"Não deu para gravar o arquivo: {ex.Message}";
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
            HttpsMessage = $"Algo deu errado: {ex.Message}";
        }
        finally
        {
            HttpsBusy = false;
        }
    }

    private static string? Describe(HttpsActionResult result, string done) => result switch
    {
        HttpsActionResult.Done => done,
        HttpsActionResult.DoneOldRootKept => done + " A CA antiga ficou na lista de confiáveis do Windows, mas sem a chave ela não assina mais nada.",
        HttpsActionResult.Declined => "O Windows não instalou a CA, então nada mudou.",
        HttpsActionResult.Cancelled => null,
        _ => "Crie uma rota primeiro.",
    };

    private void RefreshHttps()
    {
        var status = _https.Status;
        HasRoutes = _config.Current.Routes.Count > 0;
        HttpsActive = status.State == LocalCaState.Active;
        HttpsBroken = status.State is LocalCaState.NotTrusted or LocalCaState.Unreadable;
        HttpsStateText = status.State switch
        {
            LocalCaState.Active => $"Ativo. A CA vale até {status.NotAfter?.LocalDateTime.ToString("d", CultureInfo.CurrentCulture)}.",
            LocalCaState.NotTrusted => "O Windows não confia mais na CA: ela saiu da lista de confiáveis. Ative de novo ou remova.",
            LocalCaState.Unreadable => "Não foi possível ler a CA, talvez criada por outro usuário do Windows. Ative de novo ou remova.",
            _ => "Desativado. As rotas abrem só por http://.",
        };
        var firefox = HttpsActive ? FirefoxTrust.ProfilesIgnoringWindowsRoots() : [];
        FirefoxHint = firefox.Count == 0 ? null
            : $"O Firefox ({(firefox.Count == 1 ? "perfil" : "perfis")} {string.Join(", ", firefox)}) usa a própria lista de CAs e vai recusar estes certificados. " +
              $"Abra about:config, ligue {FirefoxTrust.Preference} e reinicie o Firefox.";
        CoverageText = HttpsActive
            ? "Cobre: " + HttpsService.DescribeNames(status.Names)
            : "";
        var uncovered = _https.Uncovered();
        UncoveredText = uncovered.Count switch
        {
            0 => null,
            1 => $"{uncovered[0]} está fora da CA e fica sem HTTPS até reemitir.",
            _ => $"{string.Join(", ", uncovered)} estão fora da CA e ficam sem HTTPS até reemitir.",
        };
    }

    private static void Dispatch(Action action) => Application.Current?.Dispatcher.BeginInvoke(action);

    [RelayCommand]
    private async Task ExportRoutesAsync()
    {
        var path = DialogService.PickSavePath("severino-rotas.json", "Rotas do Severino (*.json)|*.json");
        if (path is null)
            return;
        try
        {
            File.WriteAllText(path, _routes.Export());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await DialogService.ShowInfoAsync("Exportar rotas", $"Não deu para gravar o arquivo: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task ImportRoutesAsync()
    {
        var path = DialogService.PickOpenPath("Rotas do Severino (*.json)|*.json|Todos os arquivos (*.*)|*.*");
        if (path is null)
            return;

        ImportResult result;
        try
        {
            result = _routes.Import(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidRouteFileException)
        {
            await DialogService.ShowInfoAsync("Importar rotas", $"Não deu para importar: {ex.Message}");
            return;
        }

        await DialogService.ShowInfoAsync("Importar rotas", DescribeImport(result));
        await OfferReissueAsync(result.Added);
    }

    /// <summary>The summary after an import: what came in, what was already here, what was refused.</summary>
    public static string DescribeImport(ImportResult result)
    {
        var lines = new List<string>
        {
            result.Added.Count switch
            {
                0 => "Nenhuma rota nova.",
                1 => "1 rota importada.",
                var n => $"{n} rotas importadas.",
            },
        };
        if (result.Skipped.Count > 0)
            lines.Add($"Já existiam, e ficaram como estavam: {string.Join(", ", result.Skipped)}.");
        if (result.Invalid.Count > 0)
            lines.Add("Não importadas:\n" + string.Join("\n", result.Invalid.Select(i => $"• {i.Domain}: {i.Reason}")));
        return string.Join("\n\n", lines);
    }

    // Like saving a route in the form: imported HTTPS routes outside the CA need a new one.
    private async Task OfferReissueAsync(IReadOnlyList<RouteEntry> added)
    {
        var uncovered = added.Where(r => r.Https && _https.IsActive && !_https.Covers(r.Domain)).Select(r => r.Domain).ToList();
        if (uncovered.Count == 0)
            return;
        var lead = $"A CA atual não cobre {string.Join(", ", uncovered)}. Para essas rotas terem HTTPS, o Severino cria uma CA nova para todas as rotas com HTTPS.";
        await RunAsync(async () => HttpsMessage = Describe(
            await _https.ReissueAsync(prompt => DialogService.ConfirmTrustAsync("Reemitir a CA", prompt, lead)),
            done: "CA reemitida para incluir as rotas importadas."));
    }

    [RelayCommand]
    private async Task CleanAllAsync()
    {
        var (confirmed, deleteData) = await DialogService.ConfirmCleanupAsync();
        if (!confirmed)
            return;

        var result = _cleanup.Run();
        if (!result.CaRemoved)
            await DialogService.ShowInfoAsync("Limpar tudo",
                "A CA ficou na lista de confiáveis do Windows porque o pedido foi recusado. As chaves dela foram apagadas, então ela não assina mais nada.");

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
