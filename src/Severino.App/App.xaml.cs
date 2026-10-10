using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog;
using Serilog.Events;
using Severino.App.Services;
using Severino.App.Tray;
using Severino.App.ViewModels;
using Severino.Core.Configuration;
using Severino.Core.Discovery;
using Severino.Core.Dns;
using Severino.Core.Domains;
using Severino.Core.Helper;
using Severino.Core.Routes;
using Severino.Core.Wsl;
using Severino.Proxy;
using Severino.Proxy.Certificates;
using Severino.Core.Certificates;

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

        if (e.Args.Contains(CleanupArgument))
        {
            Shutdown(RunCleanup());
            return;
        }

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
        BindingErrorListener.Register(_logger);

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

        // "Iniciar com o Windows" lives in the Run key; the config only mirrors it.
        var autoStart = _host.Services.GetRequiredService<AutoStart>();
        autoStart.Repair();
        if (config.Current.Settings.StartWithWindows != autoStart.IsEnabled)
            config.Update(c => c with { Settings = c.Settings with { StartWithWindows = autoStart.IsEnabled } });

        var themes = _host.Services.GetRequiredService<ThemeService>();
        themes.Apply(config.Current.Settings.Theme);
        var window = _host.Services.GetRequiredService<MainWindow>();
        themes.Attach(window, config.Current.Settings.Theme);
        var tray = _host.Services.GetRequiredService<TrayService>();
        tray.Create();
        _host.Services.GetRequiredService<SystemProxy>().Start();

        if (loaded.Status == ConfigLoadStatus.Recovered)
            tray.ShowWarning(
                "Configuração inválida",
                $"O config.json não pôde ser lido e foi guardado em {loaded.QuarantinedPath}. O Severino começou com a configuração padrão.");

        var shell = _host.Services.GetRequiredService<ShellService>();
        _singleInstance.ActivationRequested += () => Dispatcher.BeginInvoke(shell.ShowMainWindow);

        // Started by Windows at logon: straight to the tray, whatever "Iniciar minimizado" says.
        if (!config.Current.Settings.StartMinimized && !e.Args.Contains(AutoStart.Argument))
        {
            shell.ShowMainWindow();
            OfferFirstRun(config);
        }

        // The DNS block does not follow the proxy: it stays when the app pauses or closes.
        _host.Services.GetRequiredService<ExternalHosts>().Start();
        _host.Services.GetRequiredService<DnsSync>().Start();
        // For the PowerShell module, once the config is loaded.
        _host.Services.GetRequiredService<ControlServer>().Start();
        _ = StartProxyAsync(_host.Services.GetRequiredService<ProxyCoordinator>());
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Normally ShellService.ExitAsync already stopped everything; this covers logoff and the like.
        // Run off the UI thread: the cleanup awaits, and this thread is blocked waiting for it.
        var coordinator = _host?.Services.GetService<ProxyCoordinator>();
        if (coordinator is { IsStopped: false })
            Task.Run(coordinator.StopAsync).Wait(TimeSpan.FromSeconds(5));

        var deleteData = _host?.Services.GetService<ShellService>()?.DeleteDataOnExit == true;
        _host?.Services.GetService<TrayService>()?.Dispose();
        _host?.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        _host?.Dispose();
        // After the host, so the log file is closed.
        if (deleteData)
            SystemCleanup.DeleteData(ConfigStore.DefaultDirectory);
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// The wizard, once, for someone new. A config that already has routes predates the wizard:
    /// that person needs no introduction.
    /// </summary>
    private void OfferFirstRun(ConfigService config)
    {
        if (config.Current.State.FirstRunCompleted)
            return;
        if (config.Current.Routes.Count > 0)
        {
            config.Update(c => c with { State = c.State with { FirstRunCompleted = true } });
            return;
        }
        // After the main window is up, so the wizard opens over it.
        Dispatcher.BeginInvoke(() => DialogService.ShowFirstRun(_host!.Services.GetRequiredService<FirstRunViewModel>()));
    }

    private const string CleanupArgument = "--cleanup";

    /// <summary>
    /// <c>Severino.exe --cleanup</c>, run by the uninstaller: no window, no proxy, no tray. Removes
    /// the CA, the autostart entry and the proxy exceptions of the user running it. Exit code 0
    /// when the CA is gone.
    /// </summary>
    private static int RunCleanup()
    {
        var config = new ConfigService(new ConfigStore(ConfigStore.DefaultDirectory));
        try
        {
            config.Load();
        }
        catch (UnsupportedConfigVersionException)
        {
            // A newer Severino's config: clean what does not depend on it.
        }
        var ca = new LocalCa(new CaStore(CaStore.DefaultDirectory), new WindowsTrustStore(), TimeProvider.System,
            NullLogger<LocalCa>.Instance);
        ca.Load();
        using var proxy = new SystemProxy(config, new TldDirectory(config, new WindowsDnsResolver()));
        var result = new SystemCleanup(ca, new AutoStart(), proxy).Run();
        // The WSL distros too, stopped ones included: they may keep the /etc/hosts block.
        WslCallers.RemoveAsync(new WslShell(), [.. config.Current.Settings.WslDistros], TimeSpan.FromSeconds(30)).Wait();
        return result.CaRemoved ? 0 : 1;
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
        services.AddSingleton<ServiceRouteService>();
        services.AddSingleton<ICommandRunner>(_ => new CommandRunner());
        services.AddSingleton<ServiceDiscovery>();
        services.AddSingleton(_ => new ExternalHosts());
        services.AddSingleton<DnsSync>();
        services.AddSingleton<DnsService>();
        services.AddSingleton<IWslShell>(_ => new WslShell());
        services.AddSingleton<WslCallers>();
        services.AddSingleton<IPortForwardRunner, ProcessPortForwardRunner>();
        services.AddSingleton<PortForwards>();
        services.AddSingleton<ServiceRefresher>();
        services.AddSingleton<Severino.Core.Control.ControlHandler>();
        services.AddSingleton<ControlServer>();
        services.AddSingleton<IServiceEvents, ProcessServiceEvents>();
        services.AddSingleton(sp => new ServiceWatcher(sp.GetRequiredService<ConfigService>(), sp.GetRequiredService<ServiceRefresher>(),
            sp.GetRequiredService<IServiceEvents>(), sp.GetRequiredService<ILogger<ServiceWatcher>>()));
        services.AddSingleton<IHelperClient>(_ => new HelperClient());
        services.AddSingleton<HostsSync>();
        services.AddSingleton<IDnsResolver, WindowsDnsResolver>();
        services.AddSingleton<DomainInspector>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(_ => new CaStore(CaStore.DefaultDirectory));
        services.AddSingleton<ITrustStore, WindowsTrustStore>();
        services.AddSingleton<LocalCa>();
        services.AddSingleton<TldDirectory>();
        services.AddSingleton(_ => new RequestLog());
        services.AddSingleton(sp => new ProxyServer(
            sp.GetRequiredService<ILoggerFactory>(),
            domain => sp.GetRequiredService<LocalCa>().CertificateFor(domain),
            sp.GetRequiredService<RequestLog>()));
        services.AddSingleton(_ => new HealthMonitor());
        services.AddSingleton(sp => new ServiceForwarder(sp.GetRequiredService<ILoggerFactory>(), sp.GetRequiredService<RequestLog>()));
        services.AddSingleton<ProxyCoordinator>();

        services.AddSingleton<ThemeService>();
        services.AddSingleton<ShellService>();
        // For view models the window depends on, which cannot take ShellService directly.
        services.AddSingleton(sp => new Lazy<ShellService>(sp.GetRequiredService<ShellService>));
        services.AddSingleton(_ => new AutoStart());
        services.AddSingleton<SystemCleanup>();
        services.AddSingleton(sp => new SystemProxy(sp.GetRequiredService<ConfigService>(), sp.GetRequiredService<TldDirectory>()));
        services.AddSingleton<TrayService>();
        services.AddSingleton<DialogService>();
        services.AddSingleton<HttpsService>();
        services.AddSingleton<Navigation>();

        services.AddSingleton<MainWindow>();
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<RoutesViewModel>();
        services.AddSingleton<DnsViewModel>();
        services.AddSingleton<RequestsViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddTransient<FirstRunViewModel>();
        services.AddSingleton<Func<FirstRunViewModel>>(sp => sp.GetRequiredService<FirstRunViewModel>);
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
