using System.Runtime.InteropServices;
using System.Windows;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.Extensions.DependencyInjection;
using Severino.App.ViewModels;

namespace Severino.App.Tray;

public sealed partial class TrayService(IServiceProvider services) : IDisposable
{
    public static readonly Uri IconUri = new("pack://application:,,,/Assets/severino.ico");

    private TaskbarIcon? _icon;
    private System.Drawing.Icon? _image;

    public void Create()
    {
        _icon = (TaskbarIcon)Application.Current.FindResource("TrayIcon");
        // Resolved lazily: TrayViewModel needs ShellService, which needs this service.
        Bind(_icon, services.GetRequiredService<TrayViewModel>());
        _image = LoadIcon(SmallIconSize());
        _icon.Icon = _image;
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

    /// <summary>
    /// Picks the frame drawn for <paramref name="size"/> pixels from the app's .ico, instead of
    /// letting Windows shrink a large one.
    /// </summary>
    public static System.Drawing.Icon LoadIcon(int size)
    {
        using var stream = Application.GetResourceStream(IconUri)!.Stream;
        return new System.Drawing.Icon(stream, size, size);
    }

    public void ShowInfo(string title, string message) =>
        _icon?.ShowNotification(title, message, NotificationIcon.Info);

    public void ShowWarning(string title, string message) =>
        _icon?.ShowNotification(title, message, NotificationIcon.Warning);

    public void Dispose()
    {
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
