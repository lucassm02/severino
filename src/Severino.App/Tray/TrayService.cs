using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.Extensions.DependencyInjection;
using Severino.App.ViewModels;

namespace Severino.App.Tray;

public sealed partial class TrayService(IServiceProvider services) : IDisposable
{
    public static readonly Uri IconUri = IconFor(TrayIconState.Normal);

    private TaskbarIcon? _icon;
    private System.Drawing.Icon? _image;
    private TrayViewModel? _viewModel;

    public void Create()
    {
        _icon = (TaskbarIcon)Application.Current.FindResource("TrayIcon");
        // Resolved lazily: TrayViewModel needs ShellService, which needs this service.
        _viewModel = services.GetRequiredService<TrayViewModel>();
        Bind(_icon, _viewModel);
        _viewModel.PropertyChanged += OnViewModelChanged;
        ShowState(_viewModel.IconState);
        _icon.ForceCreate(enablesEfficiencyMode: false);
    }

    public static Uri IconFor(TrayIconState state) => new(state switch
    {
        TrayIconState.Problem => "pack://application:,,,/Assets/severino-alert.ico",
        TrayIconState.Paused => "pack://application:,,,/Assets/severino-paused.ico",
        _ => "pack://application:,,,/Assets/severino.ico",
    });

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TrayViewModel.IconState) && _viewModel is not null)
            ShowState(_viewModel.IconState);
    }

    private void ShowState(TrayIconState state)
    {
        if (_icon is null)
            return;
        var previous = _image;
        _image = LoadIcon(SmallIconSize(), IconFor(state));
        _icon.Icon = _image;
        previous?.Dispose();
    }

    /// <summary>
    /// Points the icon and its menu at <paramref name="viewModel"/>. The menu opens in its own
    /// popup tree, so it gets the view model directly instead of relying on the icon to pass
    /// its DataContext along.
    /// </summary>
    public static void Bind(TaskbarIcon icon, object viewModel)
    {
        icon.DataContext = viewModel;
        if (icon.ContextMenu is { } menu)
            menu.DataContext = viewModel;
    }

    /// <summary>
    /// Picks the frame drawn for <paramref name="size"/> pixels from the app's .ico, instead of
    /// letting Windows shrink a large one.
    /// </summary>
    public static System.Drawing.Icon LoadIcon(int size, Uri? uri = null)
    {
        using var stream = Application.GetResourceStream(uri ?? IconUri)!.Stream;
        return new System.Drawing.Icon(stream, size, size);
    }

    public void ShowInfo(string title, string message) =>
        _icon?.ShowNotification(title, message, NotificationIcon.Info);

    public void ShowWarning(string title, string message) =>
        _icon?.ShowNotification(title, message, NotificationIcon.Warning);

    public void Dispose()
    {
        if (_viewModel is not null)
            _viewModel.PropertyChanged -= OnViewModelChanged;
        _icon?.Dispose();
        _icon = null;
        _image?.Dispose();
        _image = null;
    }

    /// <summary>The notification area's icon size at the current DPI (16 px at 100%).</summary>
    private static int SmallIconSize() => GetSystemMetricsForDpi(SmCxSmIcon, GetDpiForSystem());

    private const int SmCxSmIcon = 49;

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetricsForDpi(int index, uint dpi);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForSystem();
}
