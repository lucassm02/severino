using Severino.App.ViewModels;
using Severino.Core.Configuration;

namespace Severino.Tests.App;

public sealed class ServiceLinksTests
{
    private static ServiceRoute Service(bool https, params ServicePort[] ports) => new() { Names = ["catalogoapi", "catalogo.loja"], Ports = ports, Https = https };

    private static ServicePort Port(int port, bool http = false) => new() { Port = port, TargetHost = "127.0.0.1", TargetPort = port, Http = http };

    [Fact]
    public void Http_ports_and_web_looking_ones_open_and_databases_do_not()
    {
        var links = ServiceLinks.For(Service(false, Port(80, http: true), Port(3000), Port(5432)), httpsActive: true);

        Assert.Equal(["http://catalogoapi/", "http://catalogoapi:3000/"], links);
    }

    [Fact]
    public void With_https_the_main_port_opens_without_a_number_and_the_others_with_theirs()
    {
        var links = ServiceLinks.For(Service(true, Port(8080, http: true), Port(9090, http: true)), httpsActive: true);

        Assert.Equal(["https://catalogoapi/", "https://catalogoapi:9090/"], links);
    }

    [Fact]
    public void Https_counts_only_while_the_ca_is_active()
    {
        Assert.Equal(["http://catalogoapi:8080/"], ServiceLinks.For(Service(true, Port(8080, http: true)), httpsActive: false));
    }

    [Fact]
    public void A_443_of_its_own_opens_https_and_a_queue_alone_opens_nothing()
    {
        Assert.Equal(["https://catalogoapi/"], ServiceLinks.For(Service(false, Port(443)), httpsActive: false));
        Assert.Empty(ServiceLinks.For(Service(false, Port(5672)), httpsActive: false));
    }
}
