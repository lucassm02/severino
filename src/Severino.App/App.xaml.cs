using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Severino.App.Services;
using Severino.App.Tray;
using Severino.App.ViewModels;
using Severino.Core.Configuration;
using Severino.Core.Domains;
using Severino.Core.Helper;
using Severino.Core.Routes;
using Severino.Proxy;

namespace Severino.App;

public partial class App : Application
{
    private SingleInstance? _singleInstance;
    private IHost? _host;
    private Microsoft.Extensions.Logging.ILogger? _logger;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        _singleInstance = SingleInstance.TryAcquire();
        if (_singleInstance is null)
        {
            // Another Severino is running and was asked to show its window.
            Shutdown();
            return;
        }

        _host = BuildHost(e.Args);
        _host.Start();
        _logger = _host.Services.GetRequiredService<ILogger<App>>();

        var config = _host.Services.GetRequiredService<ConfigService>();
        ConfigLoadResult loaded;
        try
        {
            loaded = config.Load();
        }
        catch (UnsupportedConfigVersionException ex)
        {
            MessageBox.Show(
                $"{ex.Message}\n\nAtualize o Severino ou mova o arquivo:\n{config.Directory}",
                "Severino", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        var themes = _host.Services.GetRequiredService<ThemeService>();
        themes.Apply(config.Current.Settings.Theme);
        var window = _host.Services.GetRequiredService<MainWindow>();
        themes.Attach(window, config.Current.Settings.Theme);
        var tray = _host.Services.GetRequiredService<TrayService>();
        tray.Create();

        if (loaded.Status == ConfigLoadStatus.Recovered)
            tray.ShowWarning(
                "Configuração inválida",
                $"O config.json não pôde ser lido e foi guardado em {loaded.QuarantinedPath}. O Severino começou com a configuração padrão.");

        var shell = _host.Services.GetRequiredService<ShellService>();
        _singleInstance.ActivationRequested += () => Dispatcher.BeginInvoke(shell.ShowMainWindow);

        if (!config.Current.Settings.StartMinimized)
            shell.ShowMainWindow();

        _ = StartProxyAsync(_host.Services.GetRequiredService<ProxyCoordinator>());
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Normally ShellService.ExitAsync already stopped everything; this covers logoff and the like.
        // Run off the UI thread: the cleanup awaits, and this thread is blocked waiting for it.
        var coordinator = _host?.Services.GetService<ProxyCoordinator>();
        if (coordinator is { IsStopped: false })
            Task.Run(coordinator.StopAsync).Wait(TimeSpan.FromSeconds(5));

        _host?.Services.GetService<TrayService>()?.Dispose();
        _host?.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        _host?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private async Task StartProxyAsync(ProxyCoordinator coordinator)
    {
        try
        {
            await coordinator.StartAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to start the proxy");
        }
    }

    private static IHost BuildHost(string[] args)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { Args = args });
        var services = builder.Services;

        services.AddSerilog(log => log
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("Yarp", LogEventLevel.Warning)
            .WriteTo.File(
                Path.Combine(ConfigStore.DefaultDirectory, "logs", "app-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7));

        services.AddSingleton(_ => new ConfigStore(ConfigStore.DefaultDirectory));
        services.AddSingleton<ConfigService>();
        services.AddSingleton<RouteService>();
        services.AddSingleton<IHelperClient>(_ => new HelperClient());
        services.AddSingleton<HostsSync>();
        services.AddSingleton<IDnsResolver, WindowsDnsResolver>();
        services.AddSingleton<DomainInspector>();
        services.AddSingleton<ProxyServer>();
        services.AddSingleton(_ => new HealthMonitor());
        services.AddSingleton<ProxyCoordinator>();

        services.AddSingleton<ThemeService>();
        services.AddSingleton<ShellService>();
        services.AddSingleton<TrayService>();
        services.AddSingleton<DialogService>();

        services.AddSingleton<MainWindow>();
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<RoutesViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<StatusBarViewModel>();
        services.AddSingleton<TrayViewModel>();

        return builder.Build();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.LogError(e.Exception, "Unhandled UI exception");
        MessageBox.Show(e.Exception.ToString(), "Severino: erro inesperado", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
