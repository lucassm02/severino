using Severino.Core.Configuration;
using Severino.Core.Routes;
using Severino.Proxy;

namespace Severino.App.Services;

/// <summary>Keeps the proxy, the health monitor and the hosts block in step with the config.</summary>
public sealed class ProxyCoordinator(ConfigService config, ProxyServer proxy, HealthMonitor health, HostsSync hosts)
{
    private static readonly TimeSpan ClearTimeout = TimeSpan.FromSeconds(3);

    public async Task StartAsync()
    {
        var current = config.Current;
        health.Update(current.Routes);
        health.Start();
        hosts.Start();
        config.Changed += OnConfigChanged;
        await proxy.StartAsync(current.Settings.HttpPort, current.Routes);
    }

    /// <summary>Tries the configured port again, e.g. after the user freed it.</summary>
    public Task RetryAsync() => proxy.StartAsync(config.Current.Settings.HttpPort, config.Current.Routes);

    public bool IsStopped { get; private set; }

    /// <summary>Removes the hosts block and stops listening. Waits at most a few seconds for the Helper.</summary>
    public async Task StopAsync()
    {
        if (IsStopped)
            return;
        IsStopped = true;
        config.Changed -= OnConfigChanged;
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
        await proxy.DisposeAsync();
        await health.DisposeAsync();
    }

    private void OnConfigChanged(object? sender, SeverinoConfig updated)
    {
        health.Update(updated.Routes);
        if (updated.Settings.HttpPort != proxy.Status.Port)
            _ = proxy.StartAsync(updated.Settings.HttpPort, updated.Routes);
        else
            proxy.UpdateRoutes(updated.Routes);
    }
}
