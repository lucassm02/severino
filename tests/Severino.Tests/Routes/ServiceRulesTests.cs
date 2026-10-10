using Severino.Contracts;
using Severino.Core.Configuration;
using Severino.Core.Routes;

namespace Severino.Tests.Routes;

public sealed class ServiceRulesTests
{
    private static ServiceRoute Service(string address = "127.77.0.2", params string[] names) => new()
    {
        Names = names.Length > 0 ? names : ["catalogoapi", "catalogoapi.staging.svc.cluster.local"],
        Address = address,
        Ports = [new ServicePort { Port = 80, TargetHost = "192.168.203.100", TargetPort = 32359 }],
    };

    [Fact]
    public void Addresses_are_handed_out_in_order_skipping_taken_and_reserved_ones()
    {
        Assert.Equal("127.77.0.2", ServiceRules.NextAddress([]));
        Assert.Equal("127.77.0.4", ServiceRules.NextAddress([Service("127.77.0.2"), Service("127.77.0.3")]));

        var full = Enumerable.Range(2, 253).Select(i => Service($"127.77.0.{i}")).ToList();
        Assert.Equal("127.77.1.2", ServiceRules.NextAddress(full));
    }

    [Fact]
    public void A_valid_route_comes_back_with_its_names_normalized()
    {
        var (route, error) = ServiceRules.Validate(Service(names: ["CatalogoApi", "catalogoapi", "catalogoapi.staging."]), [], []);

        Assert.Null(error);
        Assert.Equal(["catalogoapi", "catalogoapi.staging"], route!.Names);
    }

    [Theory]
    [InlineData("localhost", "Nome inválido")]
    [InlineData("a b", "Nome inválido")]
    public void Bad_names_are_refused(string name, string expected)
    {
        Assert.StartsWith(expected, ServiceRules.Validate(Service(names: [name]), [], []).Error);
    }

    [Fact]
    public void A_name_already_routed_elsewhere_is_refused()
    {
        var other = Service("127.77.0.3", "redis");
        var web = new RouteEntry { Domain = "Meuapp.sev", Target = "http://localhost:3000" };

        Assert.Equal("redis já está em outra rota.", ServiceRules.Validate(Service(names: ["redis"]), [other], []).Error);
        Assert.Equal("meuapp.sev já está em outra rota.", ServiceRules.Validate(Service(names: ["meuapp.sev"]), [], [web]).Error);
        // Editing a route does not clash with itself.
        Assert.Null(ServiceRules.Validate(other, [other], []).Error);
    }

    [Fact]
    public void Ports_and_targets_are_checked()
    {
        ServiceRoute With(params ServicePort[] ports) => Service() with { Ports = ports };

        Assert.Equal("Informe pelo menos uma porta.", ServiceRules.Validate(With(), [], []).Error);
        Assert.Equal("A porta 80 aparece duas vezes.", ServiceRules.Validate(With(
            new() { Port = 80, TargetHost = "10.0.0.1", TargetPort = 1 },
            new() { Port = 80, TargetHost = "10.0.0.1", TargetPort = 2 }), [], []).Error);
        Assert.Equal("Use portas de 1 a 65535.", ServiceRules.Validate(With(new ServicePort { Port = 0, TargetHost = "10.0.0.1", TargetPort = 1 }), [], []).Error);
        Assert.Equal("O destino não pode ser um endereço de outra rota de serviço.",
            ServiceRules.Validate(With(new ServicePort { Port = 80, TargetHost = "127.77.0.9", TargetPort = 80 }), [], []).Error);
        Assert.Null(ServiceRules.Validate(With(new ServicePort { Port = 4000, TargetHost = "localhost", TargetPort = 24600 }), [], []).Error);
    }

    [Fact]
    public void Only_its_own_range_is_a_service_address()
    {
        Assert.Equal("Endereço de loopback inválido.", ServiceRules.Validate(Service("127.0.0.1"), [], []).Error);
        Assert.True(ServiceRules.IsServiceAddress("127.77.3.4"));
        Assert.False(ServiceRules.IsServiceAddress("127.7.3.4"));
    }

    [Fact]
    public void Hosts_entries_hold_web_routes_and_enabled_services()
    {
        var config = new SeverinoConfig
        {
            Routes = [new RouteEntry { Domain = "meuapp.sev", Target = "http://localhost:24600" }],
            Services =
            [
                Service("127.77.0.2", "catalogoapi", "catalogoapi.staging"),
                Service("127.77.0.3", "parado") with { Enabled = false },
            ],
        };

        Assert.Equal(
        [
            new HostEntry("meuapp.sev", "127.0.0.1"), new HostEntry("meuapp.sev", "::1"),
            new HostEntry("catalogoapi", "127.77.0.2"), new HostEntry("catalogoapi.staging", "127.77.0.2"),
        ], HostsEntries.For(config));
    }
}
