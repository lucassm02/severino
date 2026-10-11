using System.Windows;
using Severino.App.Resources;
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

    /// <summary>Set by "Limpar tudo" with "apagar também": the app folder goes once everything is closed.</summary>
    public bool DeleteDataOnExit { get; set; }

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

        _tray.ShowInfo(Strings.Shell_StillRunning, Strings.Shell_StillRunningText);
        _config.Update(c => c with { State = c.State with { CloseToTrayHintShown = true } });
    }

    /// <summary>Tells a new instance to wait for this one's lock instead of just showing it.</summary>
    public const string RestartArgument = "--restart";

    /// <summary>Exits and starts again, for changes read only at start, like the language.</summary>
    public async Task RestartAsync()
    {
        if (Environment.ProcessPath is not { } exe)
            return;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, RestartArgument) { UseShellExecute = false });
        await ExitAsync();
    }

    /// <summary>Removes the hosts block, stops the proxy and disposes the services before shutting down.</summary>
    public async Task ExitAsync()
    {
        if (_exiting)
            return;
        _exiting = true;
        _window.Hide();
        await _proxy.StopAsync();
        // Here, awaited, and not in App.OnExit: once Shutdown starts this thread stops running the
        // services' continuations, and waiting for them there hung the app with the tray menu on screen.
        if (Application.Current is App app)
            await app.StopServicesAsync();
        Application.Current.Shutdown();
    }
}
