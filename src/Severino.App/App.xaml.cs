using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog;
using Serilog.Events;
using Severino.App.Resources;
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

        _singleInstance = SingleInstance.TryAcquire(e.Args.Contains(ShellService.RestartArgument) ? TimeSpan.FromSeconds(20) : default);
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
                string.Format(Strings.App_ConfigTooNew, ex.Message, config.Directory),
                "Severino", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        // Before any window, view model or text: everything after this reads the chosen language.
        UiLanguage.Apply(config.Current.Settings.Language);

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
                Strings.App_ConfigInvalidTitle,
                string.Format(Strings.App_ConfigInvalid, loaded.QuarantinedPath));

        var shell = _host.Services.GetRequiredService<ShellService>();
        _singleInstance.ActivationRequested += () => Dispatcher.BeginInvoke(shell.ShowMainWindow);

        // Started by Windows at logon: straight to the tray, whatever "Iniciar minimizado" says.
        if (e.Args.Contains(ShellService.RestartArgument))
        {
            // Restarted from Settings (a new language): back where the person was.
            _host.Services.GetRequiredService<Navigation>().ShowSettings();
            shell.ShowMainWindow();
        }
        else if (!config.Current.Settings.StartMinimized && !e.Args.Contains(AutoStart.Argument))
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
        _host.Services.GetRequiredService<UpdatesViewModel>().Start();
        _host.Services.GetRequiredService<HealthNotifier>().Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Normally ShellService.ExitAsync already stopped everything; this covers logoff and the like.
        // This thread no longer pumps messages, and the services' loops started on it resume on it:
        // blocking here on their disposal never ends. The pool does what it can before the deadline.
        if (_stopping is null && _host is not null)
        {
            // The tray icon belongs to this thread; disposing it again later does nothing.
            _host.Services.GetService<TrayService>()?.Dispose();
            Task.Run(StopServicesAsync).Wait(StopDeadline);
        }

        // After the host, so the log file is closed.
        if (_deleteData)
            SystemCleanup.DeleteData(ConfigStore.DefaultDirectory);
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private static readonly TimeSpan StopDeadline = TimeSpan.FromSeconds(10);
    private Task? _stopping;
    private bool _deleteData;

    /// <summary>
    /// Stops the proxy and disposes every service. Awaited by <see cref="ShellService.ExitAsync"/>
    /// on the UI thread before <c>Shutdown</c>, while the dispatcher still runs; gives up after a
    /// few seconds so a stuck service never keeps the app open.
    /// </summary>
    internal async Task StopServicesAsync()
    {
        _stopping ??= StopHostAsync();
        await Task.WhenAny(_stopping, Task.Delay(StopDeadline));
    }

    private async Task StopHostAsync()
    {
        if (_host is null)
            return;
        _logger?.LogInformation("Stopping services");
        var coordinator = _host.Services.GetService<ProxyCoordinator>();
        if (coordinator is { IsStopped: false })
            await coordinator.StopAsync();
        _deleteData = _host.Services.GetService<ShellService>()?.DeleteDataOnExit == true;
        _host.Services.GetService<TrayService>()?.Dispose();
        await _host.StopAsync(TimeSpan.FromSeconds(5));
        _logger?.LogInformation("Services stopped; disposing them");
        // Async: several services only dispose that way, and the sync Dispose would block on them.
        if (_host is IAsyncDisposable host)
            await host.DisposeAsync();
        else
            _host.Dispose();
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
        services.AddSingleton<Backup>();
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
        services.AddSingleton<HealthNotifier>();
        services.AddSingleton<DialogService>();
        services.AddSingleton<HttpsService>();
        services.AddSingleton<Navigation>();

        services.AddSingleton<MainWindow>();
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<RoutesViewModel>();
        services.AddSingleton<ServicesViewModel>();
        services.AddSingleton<DnsViewModel>();
        services.AddSingleton<RequestsViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddTransient<FirstRunViewModel>();
        services.AddSingleton<Func<FirstRunViewModel>>(sp => sp.GetRequiredService<FirstRunViewModel>);
        services.AddSingleton<StatusBarViewModel>();
        services.AddSingleton<UpdatesViewModel>();
        services.AddSingleton<TrayViewModel>();
        services.AddSingleton<EnvironmentsViewModel>();

        return builder.Build();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.LogError(e.Exception, "Unhandled UI exception");
        MessageBox.Show(e.Exception.ToString(), Strings.App_UnexpectedError, MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
