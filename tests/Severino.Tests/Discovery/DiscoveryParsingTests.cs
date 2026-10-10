using Severino.Core.Configuration;
using Severino.Core.Discovery;

namespace Severino.Tests.Discovery;

/// <summary>kubectl and docker output, in the shapes they really print, with made-up names.</summary>
public sealed class DiscoveryParsingTests
{
    private const string Services = """
        {
          "apiVersion": "v1", "kind": "List",
          "items": [
            { "kind": "Service", "metadata": { "name": "pedidos", "namespace": "loja" },
              "spec": { "type": "NodePort", "ports": [ { "port": 80, "protocol": "TCP", "nodePort": 32001 }, { "port": 9000, "protocol": "UDP", "nodePort": 32002 } ] } },
            { "kind": "Service", "metadata": { "name": "postgres", "namespace": "dados" },
              "spec": { "type": "NodePort", "ports": [ { "port": 5432, "protocol": "TCP", "nodePort": 30711 } ] } },
            { "kind": "Service", "metadata": { "name": "api", "namespace": "loja" },
              "spec": { "type": "LoadBalancer", "ports": [ { "port": 443, "protocol": "TCP", "nodePort": 31443 } ] },
              "status": { "loadBalancer": { "ingress": [ { "ip": "10.0.0.50" } ] } } },
            { "kind": "Service", "metadata": { "name": "ingress", "namespace": "ingress-nginx" },
              "spec": { "type": "ClusterIP", "externalIPs": ["10.0.0.10"], "ports": [ { "port": 80, "protocol": "TCP" } ] } },
            { "kind": "Service", "metadata": { "name": "interno", "namespace": "loja" },
              "spec": { "type": "ClusterIP", "ports": [ { "port": 8080, "protocol": "TCP" } ] } },
            { "kind": "Service", "metadata": { "name": "fora", "namespace": "loja" },
              "spec": { "type": "ExternalName", "externalName": "exemplo.com" } }
          ]
        }
        """;

    private const string Nodes = """
        { "kind": "List", "items": [
          { "metadata": { "name": "master" }, "status": { "addresses": [ { "type": "Hostname", "address": "master" }, { "type": "InternalIP", "address": "10.0.0.1" } ] } },
          { "metadata": { "name": "node1" }, "status": { "addresses": [ { "type": "InternalIP", "address": "10.0.0.2" } ] } }
        ] }
        """;

    private const string Slices = """
        { "kind": "List", "items": [
          { "metadata": { "namespace": "loja", "labels": { "kubernetes.io/service-name": "pedidos" } },
            "endpoints": [ { "conditions": { "ready": false } }, { "conditions": { "ready": true } } ] },
          { "metadata": { "namespace": "dados", "labels": { "kubernetes.io/service-name": "postgres" } },
            "endpoints": [ { "conditions": { "ready": false } } ] },
          { "metadata": { "namespace": "loja", "labels": { "kubernetes.io/service-name": "api" } }, "endpoints": null }
        ] }
        """;

    private static DiscoveredService Find(IEnumerable<DiscoveredService> services, string name) => services.Single(s => s.Name == name);

    [Fact]
    public void Each_kubernetes_type_gets_its_destination()
    {
        var services = KubernetesDiscovery.Services(Services, "10.0.0.1", ready: null);

        Assert.Equal([new ServicePort { Port = 80, TargetHost = "10.0.0.1", TargetPort = 32001 }], Find(services, "pedidos").Ports); // UDP left out
        Assert.Equal("NodePort", Find(services, "pedidos").Kind);
        Assert.Equal([new ServicePort { Port = 443, TargetHost = "10.0.0.50", TargetPort = 443 }], Find(services, "api").Ports);
        Assert.Equal([new ServicePort { Port = 80, TargetHost = "10.0.0.10", TargetPort = 80 }], Find(services, "ingress").Ports);
        Assert.Equal("externalIPs", Find(services, "ingress").Kind);
        Assert.False(Find(services, "interno").CanImport);
        Assert.StartsWith("Só ClusterIP", Find(services, "interno").Unreachable);
        Assert.StartsWith("ExternalName", Find(services, "fora").Unreachable);
    }

    private const string IngressesJson = """
        { "kind": "List", "items": [
          { "metadata": { "name": "loja", "namespace": "loja" },
            "spec": { "rules": [ { "host": "Loja.Exemplo.com.br", "http": {} }, { "http": {} } ] },
            "status": { "loadBalancer": {} } },
          { "metadata": { "name": "api", "namespace": "loja" },
            "spec": { "rules": [ { "host": "loja.exemplo.com.br" }, { "host": "*.lojas.exemplo.com.br" } ] } }
        ] }
        """;

    [Fact]
    public void Ingress_hosts_are_read_once_each_with_their_ingresses()
    {
        var hosts = KubernetesDiscovery.Ingresses(IngressesJson);

        Assert.Equal(["*.lojas.exemplo.com.br", "loja.exemplo.com.br"], hosts.Select(h => h.Host));
        Assert.Equal(["loja/loja", "loja/api"], hosts[1].Ingresses);
    }

    [Fact]
    public void The_ingress_controller_is_found_by_its_status_or_its_service()
    {
        var services = KubernetesDiscovery.Services(Services, "10.0.0.1", null);
        var withController = services.Append(Find(services, "ingress") with { Name = "ingress-nginx-controller" }).ToList();

        Assert.Equal("http://10.0.0.10:80", KubernetesDiscovery.IngressTarget(KubernetesDiscovery.Ingresses(IngressesJson), withController));
        Assert.Equal("http://10.0.0.99:80", KubernetesDiscovery.IngressTarget([new IngressHost("a.exemplo.com", ["x/y"], "10.0.0.99")], []));
        Assert.Null(KubernetesDiscovery.IngressTarget(KubernetesDiscovery.Ingresses(IngressesJson), []));
    }

    [Fact]
    public void Kubernetes_names_come_in_four_variants()
    {
        Assert.Equal(["postgres", "postgres.dados", "postgres.dados.svc", "postgres.dados.svc.cluster.local"],
            Find(KubernetesDiscovery.Services(Services, "10.0.0.1", null), "postgres").Names);
    }

    [Fact]
    public void Without_a_node_nodeports_cannot_be_reached()
    {
        var pedidos = Find(KubernetesDiscovery.Services(Services, nodeAddress: null, ready: null), "pedidos");

        Assert.False(pedidos.CanImport);
        Assert.Equal("Escolha um nó para usar a NodePort.", pedidos.Unreachable);
    }

    [Fact]
    public void Nodes_and_ready_pods_are_read()
    {
        Assert.Equal(["10.0.0.1", "10.0.0.2"], KubernetesDiscovery.NodeAddresses(Nodes));

        var ready = KubernetesDiscovery.ReadyServices(Slices);
        Assert.Equal(["loja/pedidos"], ready);
        var services = KubernetesDiscovery.Services(Services, "10.0.0.1", ready);
        Assert.True(Find(services, "pedidos").Ready);
        Assert.False(Find(services, "postgres").Ready);
    }

    [Theory]
    [InlineData("https://192.168.203.100:6443", "192.168.203.100")]
    [InlineData("https://k8s.empresa.local:6443\n", "k8s.empresa.local")]
    [InlineData("", null)]
    public void Api_server_host(string url, string? host)
    {
        Assert.Equal(host, KubernetesDiscovery.ServerHost(url));
    }

    private const string DockerPs = """
        {"ID":"aaa","Names":"loja-api-1","Ports":"0.0.0.0:24600->4000/tcp, [::]:24600->4000/tcp","Labels":"com.docker.compose.project=loja,com.docker.compose.service=api,com.docker.compose.project.config_files=/a/compose.yml,/a/compose.override.yml","State":"running"}
        {"ID":"bbb","Names":"cache","Ports":"6379/tcp","Labels":"","State":"running"}
        {"ID":"ccc","Names":"faixa","Ports":"127.0.0.1:8000-8001->80-81/tcp, 0.0.0.0:5353->53/udp","Labels":"","State":"running"}
        {"ID":"ddd","Names":"meu_container","Ports":"0.0.0.0:9000->9000/tcp","Labels":"","State":"running"}
        """;

    [Fact]
    public void Compose_containers_are_named_after_their_service()
    {
        var api = Find(DockerDiscovery.Containers(DockerPs), "api");

        Assert.Equal("loja", api.Namespace);
        Assert.Equal(["api", "loja-api-1"], api.Names);
        Assert.Equal([new ServicePort { Port = 4000, TargetHost = "127.0.0.1", TargetPort = 24600 }], api.Ports); // IPv4 and IPv6 once
    }

    [Fact]
    public void Unpublished_ranges_udp_and_bad_names()
    {
        var containers = DockerDiscovery.Containers(DockerPs);

        Assert.Equal("Nenhuma porta publicada no host.", Find(containers, "cache").Unreachable);
        Assert.Equal(
        [
            new ServicePort { Port = 80, TargetHost = "127.0.0.1", TargetPort = 8000 },
            new ServicePort { Port = 81, TargetHost = "127.0.0.1", TargetPort = 8001 },
        ], Find(containers, "faixa").Ports);
        Assert.Equal("O nome do container não serve como nome de host.", Find(containers, "meu_container").Unreachable);
    }

    [Fact]
    public void Pasted_output_is_recognized()
    {
        Assert.Equal(6, ServiceDiscovery.FromPaste(Services, "10.0.0.1")!.Count);
        Assert.False(Find(ServiceDiscovery.FromPaste(Services, " ")!, "pedidos").CanImport);
        Assert.Equal(4, ServiceDiscovery.FromPaste("\n" + DockerPs, null)!.Count);
        Assert.Null(ServiceDiscovery.FromPaste("NAME   TYPE   CLUSTER-IP", null)); // the table, not -o json
        Assert.Null(ServiceDiscovery.FromPaste("{ \"kind\": \"Service\", quebrado", null));
    }
}
