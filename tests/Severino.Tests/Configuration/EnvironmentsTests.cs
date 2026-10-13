using Severino.Core.Configuration;
using Severino.Core.Dns;
using Severino.Core.Routes;

namespace Severino.Tests.Configuration;

public sealed class EnvironmentsTests
{
    private static DnsEntry Sql(string environment, string address) =>
        new() { Names = ["sql.banco"], Address = address, Environment = environment };

    private static SeverinoConfig Config(string active, params DnsEntry[] entries) => new()
    {
        Settings = new AppSettings { ActiveEnvironment = active },
        DnsEntries = entries,
    };

    [Fact]
    public void The_same_name_lives_in_two_environments_but_not_with_one_of_none()
    {
        var producao = Sql("producao", "192.168.100.8");
        var config = Config("producao", producao);

        Assert.Null(DnsRules.Validate(Sql("staging", "192.168.203.150"), config).Error);
        Assert.NotNull(DnsRules.Validate(Sql("", "10.0.0.8"), config).Error);
        Assert.NotNull(DnsRules.Validate(Sql("PRODUCAO", "10.0.0.8"), config).Error);
    }

    [Fact]
    public void Routes_and_services_follow_the_same_rule()
    {
        var api = new RouteEntry { Domain = "api.sev", Target = "http://localhost:3000", Environment = "producao" };
        Assert.True(RouteRules.Validate(api with { Id = Guid.NewGuid(), Environment = "staging" }, [api], 80).IsValid);
        Assert.False(RouteRules.Validate(api with { Id = Guid.NewGuid(), Environment = "" }, [api], 80).IsValid);

        var pedidos = new ServiceRoute
        {
            Names = ["pedidos"], Address = "127.77.0.2", Environment = "producao",
            Ports = [new ServicePort { Port = 80, TargetHost = "192.168.100.10", TargetPort = 30080 }],
        };
        var staging = pedidos with { Id = Guid.NewGuid(), Address = "127.77.0.3", Environment = "staging" };
        Assert.Null(ServiceRules.Validate(staging, [pedidos], []).Error);
        Assert.NotNull(ServiceRules.Validate(staging with { Environment = "" }, [pedidos], []).Error);
    }

    [Fact]
    public void Only_the_active_environment_and_none_count_and_switches_are_kept()
    {
        var producao = Sql("producao", "192.168.100.8");
        var staging = Sql("staging", "192.168.203.150") with { Enabled = false };
        var always = new DnsEntry { Names = ["nas.casa"], Address = "100.88.12.4" };
        var config = Config("producao", producao, staging, always);

        Assert.Equal(["192.168.100.8", "100.88.12.4"], DnsRules.BlockEntries(Environments.Effective(config).DnsEntries).Select(e => e.Address));

        var switched = config with { Settings = config.Settings with { ActiveEnvironment = "staging" } };
        // staging was off before and stays off: the switch belongs to the entry.
        Assert.Equal(["100.88.12.4"], DnsRules.BlockEntries(Environments.Effective(switched).DnsEntries).Select(e => e.Address));
        Assert.True(Environments.Effective(switched).DnsEntries[0] is { Enabled: false });
        Assert.True(config.DnsEntries[0].Enabled);
    }

    [Fact]
    public void No_environments_leaves_the_config_alone()
    {
        var config = Config("", new DnsEntry { Names = ["nas.casa"], Address = "100.88.12.4" });
        Assert.Same(config, Environments.Effective(config));
        Assert.Empty(Environments.List(config));
    }

    [Fact]
    public void The_first_environment_becomes_active_and_a_gone_one_leaves_none()
    {
        var empty = Config("");
        var tagged = Environments.Settle(empty, Config("", Sql("staging", "10.0.0.8")));
        Assert.Equal("staging", tagged.Settings.ActiveEnvironment);

        // A second environment does not take over.
        var two = Environments.Settle(tagged, tagged with { DnsEntries = [.. tagged.DnsEntries, Sql("producao", "10.0.0.9")] });
        Assert.Equal("staging", two.Settings.ActiveEnvironment);
        Assert.Equal(["producao", "staging"], Environments.List(two));

        // staging gone: nothing active, rather than producao on its own.
        var gone = Environments.Settle(two, two with { DnsEntries = [two.DnsEntries[1]] });
        Assert.Equal("", gone.Settings.ActiveEnvironment);
    }

    [Fact]
    public void Config_service_settles_on_every_change()
    {
        var config = new ConfigService(new ConfigStore(Path.Combine(Path.GetTempPath(), "severino-tests", Guid.NewGuid().ToString("N"))));
        config.Load();

        config.Update(c => c with { DnsEntries = [Sql("staging", "10.0.0.8")] });

        Assert.Equal("staging", config.Current.Settings.ActiveEnvironment);
    }
}
