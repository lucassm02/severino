using Severino.Core.Configuration;
using Severino.Core.Routes;
using Severino.Proxy;
using Severino.Proxy.Certificates;

namespace Severino.App.Services;

/// <summary>Keeps the proxy, the health monitor, the hosts block, HTTPS and the WSL distros in step with the config.</summary>
public sealed class ProxyCoordinator(ConfigService config, ProxyServer proxy, HealthMonitor health, HostsSync hosts, LocalCa ca, ServiceForwarder services, WslCallers wsl)
{
    private static readonly TimeSpan ClearTimeout = TimeSpan.FromSeconds(3);

    /// <summary>HTTPS listens only while the local CA is active and trusted.</summary>
    private int? HttpsPort => ca.Status.State == LocalCaState.Active ? config.Current.Settings.HttpsPort : null;

    public async Task StartAsync()
    {
        var current = config.Current;
        ca.Load();
        health.Update(current.Routes, current.Services);
        health.Start();
        hosts.Start();
        config.Changed += OnConfigChanged;
        ca.Changed += OnCaChanged;
        await proxy.StartAsync(current.Settings.HttpPort, HttpsPort, current.Routes);
        await services.UpdateAsync(current.Services);
        wsl.Start();
    }

    /// <summary>Tries the configured ports again, e.g. after the user freed them. Does nothing while paused.</summary>
    public async Task RetryAsync()
    {
        if (IsPaused)
            return;
        await proxy.StartAsync(config.Current.Settings.HttpPort, HttpsPort, config.Current.Routes);
        await services.UpdateAsync(config.Current.Services);
    }

    public bool IsStopped { get; private set; }

    /// <summary>The proxy is not listening and the hosts block is empty, until <see cref="ResumeAsync"/>.</summary>
    public bool IsPaused { get; private set; }

    /// <summary>Raised on the thread that paused or resumed.</summary>
    public event EventHandler? PausedChanged;

    /// <summary>
    /// Gets out of the way entirely: frees the ports and empties the hosts block, so the route
    /// domains resolve as they do on the internet. Not remembered: the app always starts active.
    /// </summary>
    public async Task PauseAsync()
    {
        if (IsPaused || IsStopped)
            return;
        IsPaused = true;
        PausedChanged?.Invoke(this, EventArgs.Empty);
        await ClearHostsAsync(hosts.PauseAsync);
        await proxy.StopAsync();
        await services.StopAsync();
        await wsl.PauseAsync();
    }

    public async Task ResumeAsync()
    {
        if (!IsPaused || IsStopped)
            return;
        IsPaused = false;
        PausedChanged?.Invoke(this, EventArgs.Empty);
        hosts.Resume();
        await proxy.StartAsync(config.Current.Settings.HttpPort, HttpsPort, config.Current.Routes);
        await services.UpdateAsync(config.Current.Services);
        await wsl.ResumeAsync();
    }

    /// <summary>Removes the hosts block and stops listening. Waits at most a few seconds for the Helper.</summary>
    public async Task StopAsync()
    {
        if (IsStopped)
            return;
        IsStopped = true;
        config.Changed -= OnConfigChanged;
        ca.Changed -= OnCaChanged;
        await ClearHostsAsync(hosts.ClearAsync);
        // Stop, not dispose: the DI container disposes the proxy when the host shuts down.
        await proxy.StopAsync();
        await services.StopAsync();
        await wsl.StopAsync(ClearTimeout);
        await health.DisposeAsync();
    }

    private static async Task ClearHostsAsync(Func<CancellationToken, Task> clear)
    {
        using var timeout = new CancellationTokenSource(ClearTimeout);
        try
        {
            await clear(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            // The Helper did not answer in time; the next sync rewrites the block anyway.
        }
    }

    private void OnConfigChanged(object? sender, SeverinoConfig updated)
    {
        health.Update(updated.Routes, updated.Services);
        if (IsPaused)
            return; // ResumeAsync starts from the config of that moment.

        var httpsChanged = HttpsPort is { } https ? https != proxy.HttpsStatus.Port || proxy.HttpsStatus.State != ProxyState.Running
                                                  : proxy.HttpsStatus.State == ProxyState.Running;
        if (updated.Settings.HttpPort != proxy.Status.Port || httpsChanged)
            _ = proxy.StartAsync(updated.Settings.HttpPort, HttpsPort, updated.Routes);
        else
            proxy.UpdateRoutes(updated.Routes);
        _ = services.UpdateAsync(updated.Services);
    }

    // Activated, reissued or removed: start or stop the HTTPS listener to match.
    private void OnCaChanged(object? sender, LocalCaStatus status) => _ = RetryAsync();
}
