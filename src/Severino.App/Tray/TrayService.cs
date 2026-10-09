using System.Windows;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.Extensions.DependencyInjection;
using Severino.App.ViewModels;

namespace Severino.App.Tray;

public sealed class TrayService(IServiceProvider services) : IDisposable
{
    private TaskbarIcon? _icon;

    public void Create()
    {
        _icon = (TaskbarIcon)Application.Current.FindResource("TrayIcon");
        // Resolved lazily: TrayViewModel needs ShellService, which needs this service.
        Bind(_icon, services.GetRequiredService<TrayViewModel>());
        _icon.ForceCreate(enablesEfficiencyMode: false);
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

    public void ShowInfo(string title, string message) =>
        _icon?.ShowNotification(title, message, NotificationIcon.Info);

    public void ShowWarning(string title, string message) =>
        _icon?.ShowNotification(title, message, NotificationIcon.Warning);

    public void Dispose()
    {
        _icon?.Dispose();
        _icon = null;
    }
}
