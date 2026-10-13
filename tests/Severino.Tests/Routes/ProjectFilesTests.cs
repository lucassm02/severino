using Severino.Core.Configuration;
using Severino.Core.Routes;

namespace Severino.Tests.Routes;

public sealed class ProjectFilesTests
{
    private const string Project = """
        {
          "severino": 3,
          "name": "meuapp",
          "routes": [
            { "domain": "meuapp.sev", "target": "http://localhost:3000", "https": true },
            { "domain": "meuapp.sev", "path": "/api", "target": "http://localhost:8080" }
          ],
          "services": [
            { "names": ["pedidos"], "ports": [{ "port": 80, "targetHost": "192.168.203.100", "targetPort": 30080 }] }
          ],
          "dnsEntries": [
            { "names": ["banco.interno"], "address": "10.0.0.8" }
          ]
        }
        """;

    private static SeverinoConfig Apply(SeverinoConfig config, string json) => ProjectFiles.Plan(ProjectFiles.Read(json), config).Next;

    [Fact]
    public void A_new_project_adds_everything_marked_with_its_name()
    {
        var plan = ProjectFiles.Plan(ProjectFiles.Read(Project), new SeverinoConfig());

        Assert.Empty(plan.Refused);
        Assert.All(plan.Items, i => Assert.Equal(ProjectChange.Added, i.Change));
        Assert.Equal(4, plan.Items.Count);
        Assert.All(plan.Next.Routes, r => Assert.Equal("meuapp", r.Project));
        Assert.StartsWith("127.77.", plan.Next.Services.Single().Address);
        Assert.Equal("meuapp", plan.Next.DnsEntries.Single().Project);
    }

    [Fact]
    public void Applying_again_updates_keeps_ids_and_switches_and_removes_what_left()
    {
        var first = Apply(new SeverinoConfig(), Project);
        var api = first.Routes.Single(r => r.Path == "/api");
        var service = first.Services.Single();
        // The person switched the API off here.
        first = first with { Routes = [.. first.Routes.Select(r => r.Id == api.Id ? r with { Enabled = false } : r)] };

        var changed = Project.Replace("http://localhost:8080", "http://localhost:9090").Replace("""{ "names": ["banco.interno"], "address": "10.0.0.8" }""", "");
        var plan = ProjectFiles.Plan(ProjectFiles.Read(changed), first);

        var again = plan.Next.Routes.Single(r => r.Path == "/api");
        Assert.Equal((api.Id, false, "http://localhost:9090"), (again.Id, again.Enabled, again.Target));
        Assert.Equal(service.Address, plan.Next.Services.Single().Address);
        Assert.Empty(plan.Next.DnsEntries);
        Assert.Contains(plan.Items, i => i is { Kind: ProjectItemKind.Route, Name: "meuapp.sev/api", Change: ProjectChange.Changed });
        Assert.Contains(plan.Items, i => i is { Kind: ProjectItemKind.Dns, Change: ProjectChange.Removed });
        Assert.Contains(plan.Items, i => i is { Kind: ProjectItemKind.Service, Change: ProjectChange.Unchanged });
    }

    [Fact]
    public void Items_of_others_are_left_alone_and_a_name_they_use_is_refused()
    {
        var mine = new RouteEntry { Domain = "meuapp.sev", Target = "http://localhost:5000" };
        var config = new SeverinoConfig { Routes = [mine] };

        var plan = ProjectFiles.Plan(ProjectFiles.Read(Project), config);

        Assert.Contains(plan.Next.Routes, r => r.Id == mine.Id && r.Project == "");
        Assert.Contains(plan.Refused, r => r.Domain == "meuapp.sev");
        Assert.DoesNotContain(plan.Items, i => i.Name == "meuapp.sev");
    }

    [Fact]
    public void Removing_a_project_takes_only_its_items()
    {
        var other = new DnsEntry { Names = ["nas.casa"], Address = "100.88.12.4" };
        var config = ProjectFiles.Remember(Apply(new SeverinoConfig { DnsEntries = [other] }, Project), "meuapp", @"C:\projetos\meuapp\severino.json");
        Assert.Equal(5, config.Routes.Count + config.Services.Count + config.DnsEntries.Count);
        Assert.Equal(4, ProjectFiles.Count(config, "meuapp"));

        var removed = ProjectFiles.Remove(config, "MEUAPP");

        Assert.Equal([other.Id], removed.DnsEntries.Select(d => d.Id));
        Assert.Empty(removed.Routes);
        Assert.Empty(removed.State.Projects);
    }

    [Fact]
    public void Export_writes_a_project_without_what_is_this_machines()
    {
        var config = new SeverinoConfig
        {
            Routes = [new RouteEntry { Domain = "meuapp.sev", Target = "http://banco.interno:3000", Group = "meuapp" }],
            DnsEntries = [new DnsEntry { Names = ["banco.interno"], Address = "10.0.0.8" }, new DnsEntry { Names = ["nas.casa"], Address = "100.88.12.4" }],
        };

        var json = ProjectFiles.Export("meuapp", config.Routes, config);

        Assert.StartsWith("{\n  \"severino\": 3,\n  \"name\": \"meuapp\"", json.Replace("\r\n", "\n"));
        Assert.DoesNotContain("\"id\"", json);
        Assert.DoesNotContain("nas.casa", json);
        var file = ProjectFiles.Read(json);
        Assert.Equal("banco.interno", Assert.Single(file.DnsEntries).Names[0]);
    }

    [Fact]
    public void A_backup_is_not_a_project()
    {
        var backup = RouteTransfer.Export([new RouteEntry { Domain = "a.sev", Target = "http://localhost:1" }]);
        Assert.Throws<InvalidRouteFileException>(() => ProjectFiles.Read(backup));
    }
}
