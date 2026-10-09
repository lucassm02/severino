using Severino.Core.Configuration;
using Severino.Core.Routes;
using Severino.Proxy;
using Severino.Proxy.Certificates;

namespace Severino.App.Services;

/// <summary>Keeps the proxy, the health monitor, the hosts block and HTTPS in step with the config.</summary>
public sealed class ProxyCoordinator(ConfigService config, ProxyServer proxy, HealthMonitor health, HostsSync hosts, LocalCa ca)
{
    private static readonly TimeSpan ClearTimeout = TimeSpan.FromSeconds(3);

    /// <summary>HTTPS listens only while the local CA is active and trusted.</summary>
    private int? HttpsPort => ca.Status.State == LocalCaState.Active ? config.Current.Settings.HttpsPort : null;

    public async Task StartAsync()
    {
        var current = config.Current;
        ca.Load();
        health.Update(current.Routes);
        health.Start();
        hosts.Start();
        config.Changed += OnConfigChanged;
        ca.Changed += OnCaChanged;
        await proxy.StartAsync(current.Settings.HttpPort, HttpsPort, current.Routes);
    }

    /// <summary>Tries the configured ports again, e.g. after the user freed them.</summary>
    public Task RetryAsync() => proxy.StartAsync(config.Current.Settings.HttpPort, HttpsPort, config.Current.Routes);

    public bool IsStopped { get; private set; }

    /// <summary>Removes the hosts block and stops listening. Waits at most a few seconds for the Helper.</summary>
    public async Task StopAsync()
    {
        if (IsStopped)
            return;
        IsStopped = true;
        config.Changed -= OnConfigChanged;
        ca.Changed -= OnCaChanged;
        using (var timeout = new CancellationTokenSource(ClearTimeout))
        {
            try
            {
                await hosts.ClearAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                // The Helper did not answer in time; the next start rewrites the block anyway.
            }
        }
        // Stop, not dispose: the DI container disposes the proxy when the host shuts down.
        await proxy.StopAsync();
        await health.DisposeAsync();
    }

    private void OnConfigChanged(object? sender, SeverinoConfig updated)
    {
        health.Update(updated.Routes);
        var httpsChanged = HttpsPort is { } https ? https != proxy.HttpsStatus.Port || proxy.HttpsStatus.State != ProxyState.Running
                                                  : proxy.HttpsStatus.State == ProxyState.Running;
        if (updated.Settings.HttpPort != proxy.Status.Port || httpsChanged)
            _ = proxy.StartAsync(updated.Settings.HttpPort, HttpsPort, updated.Routes);
        else
            proxy.UpdateRoutes(updated.Routes);
    }

    // Activated, reissued or removed: start or stop the HTTPS listener to match.
    private void OnCaChanged(object? sender, LocalCaStatus status) => _ = RetryAsync();
}
