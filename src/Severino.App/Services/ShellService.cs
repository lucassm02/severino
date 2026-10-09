using System.Windows;
using Severino.App.Tray;
using Severino.Core.Configuration;

namespace Severino.App.Services;

/// <summary>Shows, hides and exits the app; closing the window only sends it to the tray.</summary>
public sealed class ShellService
{
    private readonly MainWindow _window;
    private readonly TrayService _tray;
    private readonly ConfigService _config;
    private readonly ProxyCoordinator _proxy;
    private bool _exiting;

    public ShellService(MainWindow window, TrayService tray, ConfigService config, ProxyCoordinator proxy)
    {
        _window = window;
        _tray = tray;
        _config = config;
        _proxy = proxy;

        _window.Closing += (_, e) =>
        {
            if (_exiting)
                return;
            e.Cancel = true;
            HideToTray();
        };
    }

    public void ShowMainWindow()
    {
        if (_exiting)
            return;
        if (!_window.IsVisible)
            _window.Show();
        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = WindowState.Normal;

        _window.Activate();
        // Activate alone may only flash the taskbar button when another app has focus.
        _window.Topmost = true;
        _window.Topmost = false;
        _window.Focus();
    }

    public void HideToTray()
    {
        _window.Hide();

        if (_config.Current.State.CloseToTrayHintShown)
            return;

        _tray.ShowInfo("O Severino continua rodando", "Fechar a janela só esconde o Severino. Para sair, use o ícone na bandeja.");
        _config.Update(c => c with { State = c.State with { CloseToTrayHintShown = true } });
    }

    /// <summary>Removes the hosts block and stops the proxy before shutting down.</summary>
    public async Task ExitAsync()
    {
        if (_exiting)
            return;
        _exiting = true;
        _window.Hide();
        await _proxy.StopAsync();
        Application.Current.Shutdown();
    }
}
