using Microsoft.Extensions.Logging.Abstractions;
using Severino.Core.Configuration;
using Severino.Core.Discovery;
using Severino.Core.Routes;

namespace Severino.Tests.Discovery;

public sealed class PortForwardTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "severino-tests", Guid.NewGuid().ToString("N"));
    private ConfigService _config = null!;
    private readonly FakeRunner _runner = new();
    private PortForwards _forwards = null!;

    private static ServiceRoute Forwarded(string name, string source = "wsl:Ubuntu", params int[] ports) => new()
    {
        Names = [name],
        Address = "127.77.0.2",
        Ports = [.. ports.Select((p, i) => new ServicePort { Port = p, TargetHost = "127.0.0.1", TargetPort = 42000 + i })],
        Origin = new ServiceOrigin { Kind = ServiceKind.Kubernetes, Source = source, Context = "dev", Namespace = "loja", Name = name },
        PortForward = true,
    };

    public Task InitializeAsync()
    {
        _config = new ConfigService(new ConfigStore(_dir));
        _config.Load();
        _forwards = new PortForwards(_config, _runner, NullLogger<PortForwards>.Instance);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _forwards.DisposeAsync();
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void The_command_pins_context_and_namespace_and_names_itself_in_wsl()
    {
        var wsl = PortForwardSpec.For(Forwarded("fila", "wsl:Ubuntu", 5672, 15672))!.Command();
        var windows = PortForwardSpec.For(Forwarded("fila", "windows", 5672))!.Command();

        Assert.Equal("wsl.exe", wsl.Program);
        Assert.Equal(["-d", "Ubuntu", "--exec", "bash", "-lc"], wsl.Arguments.Take(5));
        Assert.StartsWith("exec -a severino-pf-", wsl.Arguments[5]);
        Assert.EndsWith("'42000:5672' '42001:15672'", wsl.Arguments[5]);
        Assert.Equal("kubectl", windows.Program);
        Assert.Equal(["--context", "dev", "-n", "loja", "port-forward", "svc/fila", "--address", "127.0.0.1", "42000:5672"], windows.Arguments);
        Assert.Null(PortForwardSpec.For(Forwarded("x") with { PortForward = false }));
    }

    [Fact]
    public void A_cluster_ip_service_imports_through_local_ports()
    {
        var service = new DiscoveredService(ServiceKind.Kubernetes, "loja", "fila", ["fila"], [], true, "ClusterIP", "Só ClusterIP", [5672, 15672]);
        var existing = Forwarded("outra", ports: 80);

        var plan = ServiceImport.Plan([new ServiceCandidate(service, new ServiceOrigin { Kind = ServiceKind.Kubernetes, Source = "wsl:Ubuntu", Context = "dev", Namespace = "loja", Name = "fila" })],
            [existing with { Address = "127.77.0.2" }], []);

        var route = plan[0].Route!;
        Assert.True(route.PortForward);
        Assert.Equal([(5672, 42001), (15672, 42002)], route.Ports.Select(p => (p.Port, p.TargetPort)));
        Assert.All(route.Ports, p => Assert.Equal("127.0.0.1", p.TargetHost));
    }

    [Fact]
    public async Task It_runs_restarts_and_stops_with_the_route()
    {
        var route = Forwarded("fila", ports: 5672);
        _config.Update(c => c with { Services = [route] });
        _forwards.Start();

        await WaitUntil(() => _forwards.StatusOf(route.Id)?.State == PortForwardState.Running);
        _runner.EndCurrent("error: lost connection to pod");
        await WaitUntil(() => _forwards.StatusOf(route.Id) is { State: PortForwardState.Restarting, Detail: "error: lost connection to pod" });
        await WaitUntil(() => _runner.Runs >= 2);

        _config.Update(c => c with { Services = [route with { Enabled = false }] });
        await WaitUntil(() => _forwards.StatusOf(route.Id) is null && _runner.Cleanups == 1);
    }

    [Fact]
    public async Task Pausing_stops_them_and_resuming_starts_them_again()
    {
        var route = Forwarded("fila", ports: 5672);
        _config.Update(c => c with { Services = [route] });
        _forwards.Start();
        await WaitUntil(() => _runner.Runs == 1);

        await _forwards.PauseAsync();
        Assert.Null(_forwards.StatusOf(route.Id));

        _forwards.Resume();
        await WaitUntil(() => _runner.Runs == 2);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
            await Task.Delay(50);
        Assert.True(condition());
    }

    private sealed class FakeRunner : IPortForwardRunner
    {
        private TaskCompletionSource<string?>? _current;
        public int Runs;
        public int Cleanups;

        public void EndCurrent(string error) => _current?.TrySetResult(error);

        public async Task<string?> RunAsync(PortForwardSpec spec, Action<string> onOutput, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Runs);
            _current = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            onOutput($"Forwarding from 127.0.0.1:{spec.Ports[0].Local} -> {spec.Ports[0].Remote}");
            using var registration = cancellationToken.Register(() => _current.TrySetCanceled());
            return await _current.Task;
        }

        public Task CleanupAsync(PortForwardSpec spec)
        {
            Interlocked.Increment(ref Cleanups);
            return Task.CompletedTask;
        }
    }
}
