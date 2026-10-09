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
        _icon.DataContext = services.GetRequiredService<TrayViewModel>();
        _icon.ForceCreate(enablesEfficiencyMode: false);
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
