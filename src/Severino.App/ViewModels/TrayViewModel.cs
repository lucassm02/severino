using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Severino.App.Services;
using Severino.Core.Configuration;

namespace Severino.App.ViewModels;

public enum TrayIconState
{
    Normal,
    Problem,
    Paused,
}

/// <summary>A route in the tray menu; clicking it opens the route in the browser.</summary>
public sealed record TrayRoute(string Domain, ICommand Open)
{
    public override string ToString() => Domain;
}

public sealed partial class TrayViewModel : ObservableObject
{
    private readonly ShellService _shell;
    private readonly ConfigService _config;
    private readonly HttpsService _https;
    private readonly ProxyCoordinator _coordinator;
    private readonly StatusBarViewModel _status;
    private readonly ILogger<TrayViewModel> _logger;

    public TrayViewModel(ShellService shell, ConfigService config, HttpsService https, ProxyCoordinator coordinator,
        StatusBarViewModel status, ILogger<TrayViewModel> logger)
    {
        _shell = shell;
        _config = config;
        _https = https;
        _coordinator = coordinator;
        _status = status;
        _logger = logger;

        RefreshRoutes();
        RefreshState();
        config.Changed += (_, _) => Dispatch(RefreshRoutes);
        https.Changed += (_, _) => Dispatch(RefreshRoutes);
        // The status bar already updates on the UI thread.
        status.PropertyChanged += OnStatusChanged;
    }

    /// <summary>The enabled routes, in the order of the list.</summary>
    public ObservableCollection<TrayRoute> Routes { get; } = [];

    [ObservableProperty]
    public partial bool HasNoRoutes { get; set; }

    [ObservableProperty]
    public partial TrayIconState IconState { get; set; }

    [ObservableProperty]
    public partial string ToolTip { get; set; } = "Severino";

    [ObservableProperty]
    public partial string PauseLabel { get; set; } = "Pausar";

    [RelayCommand]
    private void ShowWindow() => _shell.ShowMainWindow();

    [RelayCommand]
    private Task TogglePause()
    {
        _logger.LogInformation(_coordinator.IsPaused ? "Resume requested from the tray" : "Pause requested from the tray");
        return _coordinator.IsPaused ? _coordinator.ResumeAsync() : _coordinator.PauseAsync();
    }

    [RelayCommand]
    private Task Exit()
    {
        _logger.LogInformation("Exit requested from the tray");
        return _shell.ExitAsync();
    }

    private void OnStatusChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(StatusBarViewModel.HasProblem) or nameof(StatusBarViewModel.IsPaused) or nameof(StatusBarViewModel.Summary))
            RefreshState();
    }

    private void RefreshState()
    {
        IconState = _status.IsPaused ? TrayIconState.Paused : _status.HasProblem ? TrayIconState.Problem : TrayIconState.Normal;
        ToolTip = $"Severino · {_status.Summary}";
        PauseLabel = _status.IsPaused ? "Retomar" : "Pausar";
    }

    private void RefreshRoutes()
    {
        var settings = _config.Current.Settings;
        var routes = _config.Current.Routes.Where(r => r.Enabled).ToList();
        Routes.Clear();
        foreach (var route in routes)
        {
            var https = route.Https && _https.IsActive && _https.Covers(route.Domain);
            var url = Browser.UrlFor(route.Domain, settings.HttpPort, https ? settings.HttpsPort : null, route.Path);
            Routes.Add(new TrayRoute(route.Domain + route.Path, new RelayCommand(() => Browser.Open(url))));
        }
        HasNoRoutes = Routes.Count == 0;
    }

    private static void Dispatch(Action action) => Application.Current?.Dispatcher.BeginInvoke(action);
}
