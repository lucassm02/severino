using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Severino.App.Services;
using Severino.App.Tray;
using Severino.App.ViewModels;
using Severino.Core.Configuration;

namespace Severino.App;

public partial class App : Application
{
    private SingleInstance? _singleInstance;
    private IHost? _host;

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

        var window = _host.Services.GetRequiredService<MainWindow>();
        _host.Services.GetRequiredService<ThemeService>().Attach(window, config.Current.Settings.Theme);
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
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.Services.GetService<TrayService>()?.Dispose();
        _host?.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        _host?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private static IHost BuildHost(string[] args)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { Args = args });
        var services = builder.Services;

        services.AddSingleton(_ => new ConfigStore(ConfigStore.DefaultDirectory));
        services.AddSingleton<ConfigService>();
        services.AddSingleton<ThemeService>();
        services.AddSingleton<ShellService>();
        services.AddSingleton<TrayService>();

        services.AddSingleton<MainWindow>();
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<TrayViewModel>();

        return builder.Build();
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.ToString(), "Severino: erro inesperado", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
