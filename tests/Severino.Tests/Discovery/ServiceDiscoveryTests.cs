using Severino.Core.Discovery;

namespace Severino.Tests.Discovery;

public sealed class ServiceDiscoveryTests
{
    [Fact]
    public void Wsl_commands_go_through_a_login_shell_with_every_argument_quoted()
    {
        var arguments = CommandRunner.WslArguments("Ubuntu-22.04", "kubectl", ["config", "view", "-o", "jsonpath={.clusters[0].cluster.server}", "it's"], TimeSpan.FromSeconds(10));

        Assert.Equal(["-d", "Ubuntu-22.04", "--exec", "bash", "-lc",
            "timeout 10 'kubectl' 'config' 'view' '-o' 'jsonpath={.clusters[0].cluster.server}' 'it'\\''s'"], arguments);
    }

    [Fact]
    public async Task Runner_returns_output_and_reports_missing_programs()
    {
        var runner = new CommandRunner();

        var echo = await runner.RunAsync(CommandSource.Windows, "cmd.exe", ["/c", "echo severino"], CancellationToken.None);
        Assert.True(echo.Succeeded);
        Assert.Equal("severino", echo.Output.Trim());

        var missing = await runner.RunAsync(CommandSource.Windows, "nao-existe-severino.exe", [], CancellationToken.None);
        Assert.Equal(-1, missing.ExitCode);
    }

    [Fact]
    public async Task Runner_kills_what_takes_too_long()
    {
        var runner = new CommandRunner(TimeSpan.FromSeconds(1));
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var result = await runner.RunAsync(CommandSource.Windows, "ping.exe", ["-n", "30", "127.0.0.1"], CancellationToken.None);

        Assert.True(result.TimedOut);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
    }

    [Fact]
    public void Distro_list_ignores_padding_and_docker_desktop()
    {
        Assert.Equal(["Ubuntu-22.04", "Debian"], ServiceDiscovery.ParseDistros("Ubuntu-22.04\r\n\0docker-desktop\r\nDebian\r\n\r\n"));
    }

    [Theory]
    [InlineData("kubectl", true, 0, "", "O cluster não respondeu em 10 s. A VPN está conectada?")]
    [InlineData("kubectl", false, 124, "", "O cluster não respondeu em 10 s. A VPN está conectada?")]
    [InlineData("kubectl", false, 127, "bash: kubectl: command not found", "O kubectl não está instalado em WSL · Ubuntu.")]
    [InlineData("docker", false, 1, "permission denied while trying to connect to the Docker daemon socket", "Sem permissão para falar com o Docker em WSL · Ubuntu. O usuário precisa estar no grupo docker.")]
    [InlineData("docker", false, 1, "Cannot connect to the Docker daemon at unix:///var/run/docker.sock. Is the docker daemon running?", "O Docker não está rodando em WSL · Ubuntu.")]
    [InlineData("kubectl", false, 1, "E1009 memcache.go:265] ...\nerror: You must be logged in to the server (Unauthorized)", "error: You must be logged in to the server (Unauthorized)")]
    public void Failures_are_explained(string program, bool timedOut, int exitCode, string error, string expected)
    {
        Assert.Equal(expected, ServiceDiscovery.Explain(program, CommandSource.Wsl("Ubuntu"), new CommandResult(exitCode, "", error, timedOut)));
    }

    [Fact]
    public async Task Kubernetes_picks_the_api_server_node_and_reports_a_dead_cluster()
    {
        var runner = new FakeRunner
        {
            ["config current-context"] = new(0, "trabalho\n", "", false),
            ["config view --minify -o jsonpath={.clusters[0].cluster.server}"] = new(0, "https://10.0.0.2:6443", "", false),
            ["get nodes -o json --request-timeout=8s"] = new(0, """{"items":[{"status":{"addresses":[{"type":"InternalIP","address":"10.0.0.1"}]}},{"status":{"addresses":[{"type":"InternalIP","address":"10.0.0.2"}]}}]}""", "", false),
            ["get services -A -o json --request-timeout=8s"] = new(0, """{"kind":"List","items":[{"kind":"Service","metadata":{"name":"a","namespace":"n"},"spec":{"type":"NodePort","ports":[{"port":80,"protocol":"TCP","nodePort":30080}]}}]}""", "", false),
            ["get endpointslices -A -o json --request-timeout=8s"] = new(0, """{"items":[]}""", "", false),
        };

        var result = await new ServiceDiscovery(runner).KubernetesAsync(CommandSource.Wsl("Ubuntu"), CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal(("trabalho", "10.0.0.2"), (result.Context, result.Node));
        Assert.Equal("10.0.0.2", Assert.Single(result.Services).Ports.Single().TargetHost);
        Assert.False(result.Services[0].Ready);

        runner["get services -A -o json --request-timeout=8s"] = new(-1, "", "", true);
        var dead = await new ServiceDiscovery(runner).KubernetesAsync(CommandSource.Wsl("Ubuntu"), CancellationToken.None);
        Assert.Equal("O cluster não respondeu em 10 s. A VPN está conectada?", dead.Error);
        Assert.Equal("trabalho", dead.Context);
    }

    private sealed class FakeRunner : ICommandRunner
    {
        private readonly Dictionary<string, CommandResult> _answers = [];

        public CommandResult this[string arguments]
        {
            set => _answers[arguments] = value;
        }

        public Task<CommandResult> RunAsync(CommandSource source, string program, IReadOnlyList<string> arguments, CancellationToken cancellationToken) =>
            Task.FromResult(_answers.TryGetValue(string.Join(' ', arguments), out var answer) ? answer : new CommandResult(127, "", "not found", false));
    }
}
