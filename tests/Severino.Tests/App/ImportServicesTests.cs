using Severino.App.ViewModels;
using Severino.Core.Configuration;
using Severino.Core.Discovery;
using Severino.Core.Routes;

namespace Severino.Tests.App;

public sealed class ImportServicesTests : IDisposable
{
    private const string ServicesJson = """
        {"kind":"List","items":[
          {"kind":"Service","metadata":{"name":"pedidos","namespace":"loja"},"spec":{"type":"NodePort","ports":[{"port":80,"protocol":"TCP","nodePort":30001}]}},
          {"kind":"Service","metadata":{"name":"api","namespace":"loja"},"spec":{"type":"NodePort","ports":[{"port":80,"protocol":"TCP","nodePort":30002}]}},
          {"kind":"Service","metadata":{"name":"interno","namespace":"loja"},"spec":{"type":"ClusterIP","ports":[{"port":80,"protocol":"TCP"}]}},
          {"kind":"Service","metadata":{"name":"redis","namespace":"cache"},"spec":{"type":"NodePort","ports":[{"port":6379,"protocol":"TCP","nodePort":30003}]}}
        ]}
        """;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "severino-tests", Guid.NewGuid().ToString("N"));
    private readonly ConfigService _config;
    private readonly ServiceRouteService _services;

    public ImportServicesTests()
    {
        _config = new ConfigService(new ConfigStore(_dir));
        _config.Load();
        _services = new ServiceRouteService(_config);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private async Task<ImportServicesViewModel> OpenAsync()
    {
        var viewModel = new ImportServicesViewModel(new ServiceDiscovery(new Runner()), _services, _config);
        await viewModel.InitializeAsync();
        return viewModel;
    }

    [Fact]
    public async Task Opens_on_the_distro_that_has_services()
    {
        var viewModel = await OpenAsync();

        Assert.Equal(["Windows", "WSL · Ubuntu", "WSL · Debian (parada; procurar inicia a distro)", "Colar saída de comando"], viewModel.Sources.Select(s => s.Label));
        Assert.Equal("WSL · Ubuntu", viewModel.SelectedSource!.Label);
        Assert.Equal("trabalho · 4 services, 3 com acesso de fora", viewModel.KubernetesStatus);
        Assert.True(viewModel.DockerFailed);
        Assert.Equal(4, viewModel.Visible.Count);
    }

    [Fact]
    public async Task A_whole_namespace_is_marked_and_imported()
    {
        var viewModel = await OpenAsync();
        viewModel.SelectedNamespace = "loja";
        Assert.Equal(3, viewModel.Visible.Count);

        viewModel.SelectNamespaceCommand.Execute(null);

        Assert.Equal(2, viewModel.SelectedCount); // interno has no outside access
        Assert.Equal("Desmarcar o namespace", viewModel.SelectNamespaceLabel);
        viewModel.ImportCommand.Execute(null);
        Assert.Equal(["api", "pedidos"], _services.Services.Select(s => s.Names[0]).Order());
        Assert.Equal("wsl:Ubuntu", _services.Services[0].Origin!.Source);
        Assert.Equal("2 serviços importados", ImportServicesViewModel.Describe(viewModel.Result!));
    }

    [Fact]
    public async Task Search_and_name_clashes_show_on_the_rows()
    {
        _config.Update(c => c with { Routes = [new RouteEntry { Domain = "redis.cache", Target = "http://localhost:6379" }] });
        var viewModel = await OpenAsync();

        viewModel.Search = "redis";

        var redis = Assert.Single(viewModel.Visible);
        Assert.Equal("redis.cache já está em outra rota e fica de fora.", redis.Warning);
    }

    [Fact]
    public async Task A_node_with_a_dns_name_is_used_by_name()
    {
        var viewModel = new ImportServicesViewModel(new ServiceDiscovery(new Runner()), _services, _config,
            [new Severino.Core.Dns.DnsDestination("gateway.k8s", "10.0.0.1", Outside: false)]);
        await viewModel.InitializeAsync();

        Assert.Equal("gateway.k8s", viewModel.SelectedNode);
        Assert.Equal(["10.0.0.1", "gateway.k8s"], viewModel.Nodes);
        var pedidos = viewModel.Visible.Single(i => i.Name == "pedidos");
        Assert.Equal("gateway.k8s", pedidos.Candidate.Service.Ports[0].TargetHost);

        viewModel.SelectedNode = "10.0.0.1";

        Assert.Equal("10.0.0.1", viewModel.Visible.Single(i => i.Name == "pedidos").Candidate.Service.Ports[0].TargetHost);
    }

    [Fact]
    public async Task Unchecked_tools_are_not_asked_and_hide_their_rows()
    {
        _config.Update(c => c with { State = c.State with { ImportKubernetes = false } });
        var runner = new Runner();
        var viewModel = new ImportServicesViewModel(new ServiceDiscovery(runner), _services, _config);
        await viewModel.InitializeAsync();

        Assert.DoesNotContain(runner.Asked, a => a.StartsWith("kubectl"));
        Assert.Null(viewModel.KubernetesStatus);
        Assert.False(viewModel.HasCandidates);

        viewModel.SelectedSource = viewModel.Sources.Single(s => s.Label == "WSL · Ubuntu");
        viewModel.UseKubernetes = true;
        await Task.Delay(50);
        Assert.Equal(4, viewModel.Visible.Count);
        Assert.True(_config.Current.State.ImportKubernetes);

        viewModel.UseKubernetes = false;
        Assert.Empty(viewModel.Visible);
        Assert.True(viewModel.DockerFailed);
    }

    private sealed class Runner : ICommandRunner
    {
        public List<string> Asked { get; } = [];

        public Task<CommandResult> RunAsync(CommandSource source, string program, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            var args = string.Join(' ', arguments);
            lock (Asked)
                Asked.Add($"{program} {args}");
            CommandResult Ok(string output) => new(0, output, "", false);
            return Task.FromResult((source.Distro, program, args) switch
            {
                (null, "wsl.exe", "--list --running --quiet") => Ok("Ubuntu\n"),
                (null, "wsl.exe", "--list --quiet") => Ok("Ubuntu\nDebian\n"),
                ("Ubuntu", "kubectl", "config current-context") => Ok("trabalho\n"),
                ("Ubuntu", "kubectl", "config view --minify -o jsonpath={.clusters[0].cluster.server}") => Ok("https://10.0.0.1:6443"),
                ("Ubuntu", "kubectl", "get nodes -o json --request-timeout=8s") => Ok("""{"items":[{"status":{"addresses":[{"type":"InternalIP","address":"10.0.0.1"}]}}]}"""),
                ("Ubuntu", "kubectl", "get services -A -o json --request-timeout=8s") => Ok(ServicesJson),
                ("Ubuntu", "kubectl", "get endpointslices -A -o json --request-timeout=8s") => Ok("""{"items":[]}"""),
                ("Ubuntu", "docker", _) => new CommandResult(1, "", "Cannot connect to the Docker daemon at unix:///var/run/docker.sock.", false),
                _ => new CommandResult(-1, "", "not installed", false),
            });
        }
    }
}
