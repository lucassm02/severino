using Microsoft.Extensions.Logging.Abstractions;
using Severino.Core.Configuration;
using Severino.Core.Discovery;
using Severino.Core.Routes;

namespace Severino.Tests.Discovery;

public sealed class ServiceWatcherTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "severino-tests", Guid.NewGuid().ToString("N"));
    private readonly Runner _runner = new();
    private readonly Events _events = new();
    private ConfigService _config = null!;
    private ServiceRouteService _services = null!;
    private ServiceWatcher _watcher = null!;

    public Task InitializeAsync()
    {
        _config = new ConfigService(new ConfigStore(_dir));
        _config.Load();
        _services = new ServiceRouteService(_config);
        var refresher = new ServiceRefresher(new ServiceDiscovery(_runner), _services);
        _watcher = new ServiceWatcher(_config, refresher, _events, NullLogger<ServiceWatcher>.Instance,
            kubernetesInterval: TimeSpan.FromHours(1), debounce: TimeSpan.FromMilliseconds(50));
        _config.Update(c => c with
        {
            Services =
            [
                new ServiceRoute
                {
                    Names = ["orchestrator"],
                    Address = "127.77.0.2",
                    Ports = [new ServicePort { Port = 4000, TargetHost = "127.0.0.1", TargetPort = 24600 }],
                    Origin = new ServiceOrigin { Kind = ServiceKind.Docker, Source = "wsl:Ubuntu", Context = "E1", Namespace = "meuapp", Name = "orchestrator" },
                },
            ],
        });
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _watcher.DisposeAsync();
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task A_docker_event_refreshes_the_routes_and_tells()
    {
        string? told = null;
        _watcher.Updated += (_, message) => told = message;
        _watcher.Start();
        await WaitUntil(() => _events.Watching);

        _runner.PublishedPort = 24700;
        _events.Fire();
        _events.Fire(); // a burst becomes one refresh

        await WaitUntil(() => told is not null);
        Assert.Equal("orchestrator com portas novas, atualizado sozinho.", told);
        Assert.Equal(24700, _services.Services[0].Ports[0].TargetPort);
    }

    [Fact]
    public async Task Switched_off_it_follows_nothing()
    {
        _config.Update(c => c with { Settings = c.Settings with { WatchServices = false } });
        _watcher.Start();
        await Task.Delay(200);

        Assert.False(_events.Watching);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
            await Task.Delay(50);
        Assert.True(condition());
    }

    private sealed class Events : IServiceEvents
    {
        private Action? _onChange;
        public bool Watching => _onChange is not null;
        public void Fire() => _onChange?.Invoke();

        public async Task<string?> WatchDockerAsync(CommandSource source, string marker, Action onChange, CancellationToken cancellationToken)
        {
            _onChange = onChange;
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return null;
        }

        public Task CleanupAsync(CommandSource source, string marker) => Task.CompletedTask;
    }

    private sealed class Runner : ICommandRunner
    {
        public int PublishedPort { get; set; } = 24600;

        public Task<CommandResult> RunAsync(CommandSource source, string program, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            var args = string.Join(' ', arguments);
            return Task.FromResult((program, args) switch
            {
                ("docker", "info --format {{.ID}}") => new CommandResult(0, "E1", "", false),
                ("docker", "ps --format json") => new CommandResult(0,
                    $$"""{"ID":"a","Names":"meuapp-orchestrator-1","Ports":"0.0.0.0:{{PublishedPort}}->4000/tcp","Labels":"com.docker.compose.project=meuapp,com.docker.compose.service=orchestrator","State":"running"}""", "", false),
                _ => new CommandResult(-1, "", "", false),
            });
        }
    }
}
