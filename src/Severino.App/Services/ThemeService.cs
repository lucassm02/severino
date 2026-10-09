using System.Windows;
using Microsoft.Win32;
using Severino.Core.Configuration;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Severino.App.Services;

public sealed class ThemeService
{
    private Window? _window;
    private bool _watching;

    public void Attach(Window window, AppTheme theme)
    {
        _window = window;
        Apply(theme);
    }

    /// <summary>
    /// Switches the app's resources to <paramref name="theme"/>. Call it once before the first
    /// window is created: some WPF-UI text keeps the brush it saw when it was built.
    /// </summary>
    public void Apply(AppTheme theme)
    {
        var resolved = theme switch
        {
            AppTheme.Dark => ApplicationTheme.Dark,
            AppTheme.Light => ApplicationTheme.Light,
            _ => SystemPrefersDark() ? ApplicationTheme.Dark : ApplicationTheme.Light,
        };
        ApplicationThemeManager.Apply(resolved, WindowBackdropType.Mica);

        if (_window is null)
            return;

        if (theme == AppTheme.Auto)
        {
            if (!_watching)
                SystemThemeWatcher.Watch(_window, WindowBackdropType.Mica);
            _watching = true;
        }
        else
        {
            // UnWatch throws for a window that was never watched or is not loaded yet.
            if (_watching)
                SystemThemeWatcher.UnWatch(_window);
            _watching = false;
        }
    }

    private static bool SystemPrefersDark()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is 0;
    }
}
