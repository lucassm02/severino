using Severino.Core.Certificates;
using Severino.Core.Configuration;
using Severino.Core.Routes;
using Severino.Proxy;

namespace Severino.Tests.Routes;

/// <summary>Service ports marked HTTP: validation, CA coverage, the proxy's internal routes, refreshes.</summary>
public sealed class ServiceHttpRulesTests
{
    private static ServiceRoute Service(bool https = false, params ServicePort[] ports) => new()
    {
        Names = ["catalogoapi", "catalogo.loja"],
        Address = "127.77.0.2",
        Ports = ports.Length > 0 ? ports : [Port(8080, http: true), Port(5432)],
        Https = https,
    };

    private static ServicePort Port(int port, bool http = false) => new() { Port = port, TargetHost = "192.168.203.100", TargetPort = 30000 + port % 1000, Http = http };

    [Fact]
    public void Https_and_rules_need_an_http_port()
    {
        var tcpOnly = Service(ports: Port(5432));

        Assert.Equal("O HTTPS precisa de uma porta marcada como HTTP.", ServiceRules.Validate(tcpOnly with { Https = true }, [], []).Error);
        Assert.StartsWith("As regras só valem nas portas marcadas como HTTP", ServiceRules.Validate(tcpOnly with { Rules = new TrafficRules { Cors = true } }, [], []).Error);
        Assert.Null(ServiceRules.Validate(Service(https: true) with { Rules = new TrafficRules { Cors = true } }, [], []).Error);
    }

    [Fact]
    public void Bad_rules_are_refused_like_a_routes()
    {
        var error = ServiceRules.Validate(Service() with { Rules = new TrafficRules { DelayMs = -1 } }, [], []).Error;

        Assert.Equal(RouteRules.ValidateRules(new TrafficRules { DelayMs = -1 }), error);
    }

    [Fact]
    public void The_main_port_is_80_when_http_else_the_first_http_one()
    {
        Assert.Equal(8080, ServiceHttp.Main(Service())!.Port);
        Assert.Equal(80, ServiceHttp.Main(Service(ports: [Port(8080, http: true), Port(80, http: true)]))!.Port);
        Assert.Null(ServiceHttp.Main(Service(ports: Port(5432))));
    }

    [Fact]
    public void Https_adds_443_unless_the_service_has_its_own()
    {
        Assert.Equal(443, ServiceHttp.ExtraTlsPort(Service(https: true)));
        Assert.Null(ServiceHttp.ExtraTlsPort(Service(https: false)));
        Assert.Null(ServiceHttp.ExtraTlsPort(Service(https: true, Port(8080, http: true), Port(443))));
    }

    [Fact]
    public void Https_services_count_for_the_ca()
    {
        var domains = CaCoverage.RequiredDomains([], [Service(https: true), Service() with { Names = ["semtls"] }]);

        Assert.Equal(["catalogo.loja", "catalogoapi"], domains);
    }

    [Fact]
    public void Each_http_port_is_an_internal_route_by_name_and_port()
    {
        var service = Service(https: true);

        var (routes, clusters) = ProxyConfigMapper.Map([], 80, 443, [service]);

        var route = Assert.Single(routes);
        Assert.Equal(["catalogoapi:8080", "catalogo.loja:8080", "catalogoapi:443", "catalogo.loja:443"], route.Match.Hosts);
        Assert.Equal(service.Id.ToString("N"), route.Metadata![ProxyConfigMapper.ServiceKey]);
        Assert.Equal("http://192.168.203.100:30080/", Assert.Single(clusters).Destinations!["target"].Address);
        Assert.Contains(route.Transforms!, t => t.ContainsKey("RequestHeaderOriginalHost"));
    }

    [Fact]
    public void Disabled_services_and_tcp_ports_get_no_internal_route()
    {
        Assert.Empty(ProxyConfigMapper.Map([], 80, 443, [Service() with { Enabled = false }]).Routes);
        Assert.Empty(ProxyConfigMapper.Map([], 80, 443, [Service(ports: Port(5432))]).Routes);
    }

    [Fact]
    public void A_refresh_keeps_the_http_mark_of_the_ports_still_there()
    {
        IReadOnlyList<ServicePort> old = [Port(8080, http: true), Port(5432)];
        IReadOnlyList<ServicePort> fresh = [Port(8080) with { TargetPort = 31111 }, Port(9090)];

        var kept = ServiceHttp.KeepHttp(fresh, old);

        Assert.True(kept[0].Http);
        Assert.Equal(31111, kept[0].TargetPort);
        Assert.False(kept[1].Http);
    }
}
