using Severino.Core.Configuration;
using Severino.Core.Discovery;
using Severino.Core.Routes;

namespace Severino.Tests.Routes;

public sealed class ServiceImportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "severino-tests", Guid.NewGuid().ToString("N"));
    private readonly ConfigService _config;
    private readonly ServiceRouteService _services;

    public ServiceImportTests()
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

    private static ServiceCandidate Kube(string ns, string name, int nodePort = 30080, string context = "trabalho") => new(
        new DiscoveredService(ServiceKind.Kubernetes, ns, name, KubernetesDiscovery.NameVariants(name, ns),
            [new ServicePort { Port = 80, TargetHost = "10.0.0.1", TargetPort = nodePort }], Ready: true, "NodePort"),
        new ServiceOrigin { Kind = ServiceKind.Kubernetes, Source = "wsl:Ubuntu", Context = context, Namespace = ns, Name = name });

    [Fact]
    public void Each_new_service_gets_the_next_address_and_its_names()
    {
        var plan = ServiceImport.Plan([Kube("loja", "pedidos"), Kube("loja", "api")], [], []);

        Assert.Equal(["127.77.0.2", "127.77.0.3"], plan.Select(p => p.Route!.Address));
        Assert.Equal(["pedidos", "pedidos.loja", "pedidos.loja.svc", "pedidos.loja.svc.cluster.local"], plan[0].Route!.Names);
        Assert.All(plan, p => Assert.Null(p.Warning));
    }

    [Fact]
    public void Two_namespaces_with_the_same_service_both_import_without_the_short_name_twice()
    {
        var plan = ServiceImport.Plan([Kube("loja", "redis"), Kube("cache", "redis")], [], []);

        Assert.Contains("redis", plan[0].Route!.Names);
        Assert.Equal(["redis.cache", "redis.cache.svc", "redis.cache.svc.cluster.local"], plan[1].Route!.Names);
        Assert.Equal("redis já está em outra rota e fica de fora.", plan[1].Warning);
    }

    [Fact]
    public void Names_of_web_routes_are_left_out_and_a_service_with_no_name_left_is_not_imported()
    {
        var web = new RouteEntry { Domain = "pedidos.loja", Target = "http://localhost:3000" };
        var unreachable = Kube("loja", "interno") with
        {
            Service = Kube("loja", "interno").Service with { Ports = [], Unreachable = "Só ClusterIP: não tem acesso de fora do cluster." },
        };
        var taken = new ServiceRoute { Names = ["x", "x.loja", "x.loja.svc", "x.loja.svc.cluster.local"], Address = "127.77.0.2", Ports = Kube("loja", "x").Service.Ports };

        var plan = ServiceImport.Plan([Kube("loja", "pedidos"), unreachable, Kube("loja", "x")], [taken], [web]);

        Assert.Equal(["pedidos", "pedidos.loja.svc", "pedidos.loja.svc.cluster.local"], plan[0].Route!.Names);
        Assert.Null(plan[1].Route);
        Assert.StartsWith("Só ClusterIP", plan[1].Error);
        Assert.Equal("Todos os nomes já estão em outras rotas.", plan[2].Error);
    }

    [Fact]
    public void Importing_again_updates_ports_and_keeps_names_and_address()
    {
        _services.Import([Kube("loja", "pedidos")]);
        var first = Assert.Single(_services.Services);
        _services.Save(first with { Names = ["pedidos"] });

        var plan = _services.Import([Kube("loja", "pedidos", nodePort: 31000)]);

        Assert.Equal(first.Id, plan[0].Replaces!.Id);
        var again = Assert.Single(_services.Services);
        Assert.Equal((first.Id, first.Address), (again.Id, again.Address));
        Assert.Equal(["pedidos"], again.Names);
        Assert.Equal(31000, again.Ports[0].TargetPort);
    }

    [Fact]
    public void Refresh_moves_ports_and_reports_what_is_gone()
    {
        _services.Import([Kube("loja", "pedidos"), Kube("loja", "api"), Kube("loja", "velho"), Kube("loja", "outro", context: "outro")]);
        var source = new ServiceOrigin { Kind = ServiceKind.Kubernetes, Source = "wsl:Ubuntu", Context = "trabalho" };

        var result = _services.Refresh(source, [Kube("loja", "pedidos", nodePort: 32000).Service, Kube("loja", "api").Service]);

        Assert.Equal("pedidos", Assert.Single(result.Updated).Names[0]);
        Assert.Equal("velho", Assert.Single(result.Missing).Names[0]); // "outro" is from another context
        Assert.Equal(32000, _services.Services.Single(s => s.Names[0] == "pedidos").Ports[0].TargetPort);
        Assert.Equal(4, _services.Services.Count);
    }

    [Fact]
    public void Removed_services_come_back_unless_their_names_were_taken()
    {
        _services.Import([Kube("loja", "pedidos"), Kube("loja", "api")]);
        var removed = _services.Remove([.. _services.Services.Select(s => s.Id)]);
        Assert.Empty(_services.Services);

        _services.Import([Kube("loja", "api", context: "outro")]);

        Assert.Equal(1, _services.Restore(removed));
        Assert.Equal(["api", "pedidos"], _services.Services.Select(s => s.Names[0]).Order());
        // The new api took 127.77.0.2, so pedidos comes back on another address.
        Assert.Equal("127.77.0.3", _services.Services.Single(s => s.Names[0] == "pedidos").Address);
    }

    [Fact]
    public void A_web_route_cannot_take_a_service_name()
    {
        _services.Import([Kube("loja", "pedidos")]);

        var errors = new RouteService(_config).Validate(new RouteEntry { Domain = "pedidos.loja", Target = "http://localhost:3000" });

        Assert.Equal("Já existe uma rota de serviço com este nome.", errors.Domain);
    }

    [Theory]
    [InlineData("80 → 192.168.203.100:32359", 80, "192.168.203.100", 32359)]
    [InlineData(" 5432 -> db.local:15432 ", 5432, "db.local", 15432)]
    [InlineData("443 [::1]:8443", 443, "::1", 8443)]
    [InlineData("8080=localhost:3000", 8080, "localhost", 3000)]
    public void Port_lines_are_read(string line, int port, string host, int targetPort)
    {
        Assert.True(ServiceRules.TryParsePort(line, out var parsed));
        Assert.Equal(new ServicePort { Port = port, TargetHost = host, TargetPort = targetPort }, parsed);
    }

    [Theory]
    [InlineData("80")]
    [InlineData("80 → host")]
    [InlineData("http://host:80")]
    public void Bad_port_lines_are_refused(string line)
    {
        Assert.False(ServiceRules.TryParsePort(line, out _));
    }
}
