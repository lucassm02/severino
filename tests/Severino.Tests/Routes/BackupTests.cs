using Severino.Core.Configuration;
using Severino.Core.Dns;
using Severino.Core.Routes;

namespace Severino.Tests.Routes;

public sealed class BackupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "severino-tests", Guid.NewGuid().ToString("N"));
    private readonly List<ExternalHosts> _hosts = [];

    public void Dispose()
    {
        foreach (var hosts in _hosts)
            hosts.Dispose();
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    /// <summary>A machine of its own: config, an empty hosts file and the backup over them.</summary>
    private (ConfigService Config, Backup Backup) Machine(string name)
    {
        var dir = Path.Combine(_dir, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "hosts"), "");
        var config = new ConfigService(new ConfigStore(dir));
        config.Load();
        var external = new ExternalHosts(Path.Combine(dir, "hosts"));
        _hosts.Add(external);
        return (config, new Backup(config, new ServiceRouteService(config, external), new DnsService(config, external, new FakeHelper())));
    }

    [Fact]
    public void Routes_services_and_dns_entries_travel_together()
    {
        var (from, backup) = Machine("de");
        from.Update(c => c with
        {
            Routes = [new RouteEntry { Domain = "callfred.sev", Target = "http://gateway.k8s:80", Group = "callfred" }],
            Services =
            [
                new ServiceRoute { Names = ["pedidos"], Address = "127.77.0.9", Ports = [new ServicePort { Port = 80, TargetHost = "gateway.k8s", TargetPort = 30080 }] },
                new ServiceRoute { Names = ["fila"], Address = "127.77.0.10", PortForward = true, Ports = [new ServicePort { Port = 5672, TargetHost = "127.0.0.1", TargetPort = 42007 }] },
            ],
            DnsEntries = [new DnsEntry { Names = ["gateway.k8s"], Address = "10.0.0.1" }],
        });
        var json = backup.Export();

        var (to, restore) = Machine("para");
        to.Update(c => c with { Services = [new ServiceRoute { Names = ["redis"], Address = "127.77.0.2", Ports = [new ServicePort { Port = 6379, TargetHost = "10.0.0.2", TargetPort = 6379 }] }] });
        var result = restore.Import(json);

        Assert.Equal(["callfred.sev"], result.Routes.Added.Select(r => r.Domain));
        Assert.Equal(["pedidos", "fila"], result.Services);
        Assert.Equal(["gateway.k8s"], result.Dns);
        Assert.Empty(result.Refused);
        var services = to.Current.Services;
        Assert.Equal(["127.77.0.2", "127.77.0.3", "127.77.0.4"], services.Select(s => s.Address));
        Assert.Equal(42000, services.Single(s => s.PortForward).Ports[0].TargetPort);
        Assert.Equal("callfred", to.Current.Routes[0].Group);
        Assert.DoesNotContain(services, s => from.Current.Services.Any(f => f.Id == s.Id));
    }

    [Fact]
    public void Names_already_used_here_are_left_out()
    {
        var (from, backup) = Machine("de");
        from.Update(c => c with
        {
            Services = [new ServiceRoute { Names = ["pedidos"], Address = "127.77.0.2", Ports = [new ServicePort { Port = 80, TargetHost = "10.0.0.1", TargetPort = 30080 }] }],
            DnsEntries = [new DnsEntry { Names = ["sql.interno"], Address = "10.0.0.8" }],
        });
        var json = backup.Export();
        var (to, restore) = Machine("para");
        to.Update(c => c with { DnsEntries = [new DnsEntry { Names = ["sql.interno"], Address = "10.0.0.9" }] });

        var first = restore.Import(json);
        var again = restore.Import(json);

        Assert.Equal(["pedidos"], first.Services);
        Assert.Equal(["sql.interno"], first.Refused.Select(r => r.Domain));
        Assert.Equal("10.0.0.9", Assert.Single(to.Current.DnsEntries).Address);
        Assert.Empty(again.Services);
        Assert.Equal(["sql.interno", "pedidos"], again.Refused.Select(r => r.Domain));
    }

    [Fact]
    public void A_file_with_only_routes_still_imports()
    {
        var (to, restore) = Machine("para");

        var result = restore.Import("""{ "severino": 1, "routes": [ { "domain": "a.sev", "target": "http://localhost:3000" } ] }""");

        Assert.Equal(["a.sev"], to.Current.Routes.Select(r => r.Domain));
        Assert.Empty(result.Services);
    }
}
