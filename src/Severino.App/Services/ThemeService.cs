using System.Windows;
using Microsoft.Win32;
using Severino.Core.Configuration;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Severino.App.Services;

public sealed class ThemeService
{
    private Window? _window;

    public void Attach(Window window, AppTheme theme)
    {
        _window = window;
        Apply(theme);
    }

    public void Apply(AppTheme theme)
    {
        if (_window is null)
            return;

        if (theme == AppTheme.Auto)
        {
            ApplicationThemeManager.Apply(SystemPrefersDark() ? ApplicationTheme.Dark : ApplicationTheme.Light, WindowBackdropType.Mica);
            SystemThemeWatcher.Watch(_window, WindowBackdropType.Mica);
            return;
        }

        SystemThemeWatcher.UnWatch(_window);
        ApplicationThemeManager.Apply(theme == AppTheme.Dark ? ApplicationTheme.Dark : ApplicationTheme.Light, WindowBackdropType.Mica);
    }

    private static bool SystemPrefersDark()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is 0;
    }
}
