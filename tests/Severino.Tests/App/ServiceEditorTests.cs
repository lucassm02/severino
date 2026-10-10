using Severino.App.ViewModels;
using Severino.Core.Configuration;
using Severino.Core.Routes;

namespace Severino.Tests.App;

public sealed class ServiceEditorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "severino-tests", Guid.NewGuid().ToString("N"));
    private readonly ConfigService _config;
    private readonly ServiceRouteService _services;

    public ServiceEditorTests()
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

    [Fact]
    public void A_draft_from_the_route_form_is_saved_as_a_new_service()
    {
        var draft = new ServiceRoute { Names = ["banco.sev"], Ports = [new ServicePort { Port = 5432, TargetHost = "localhost", TargetPort = 5432 }] };
        var form = new ServiceEditorViewModel(_services, null, draft: draft);

        Assert.True(form.IsNew);
        Assert.Equal(("5432", "localhost", "5432"), (form.Ports[0].Port, form.Ports[0].Host, form.Ports[0].TargetPort));
        form.SaveCommand.Execute(null);

        var saved = Assert.Single(_config.Current.Services);
        Assert.Equal(["banco.sev"], saved.Names);
        Assert.StartsWith("127.77.", saved.Address);
    }

    [Fact]
    public void Ports_are_rows_and_blank_ones_are_left_out()
    {
        var form = new ServiceEditorViewModel(_services, null) { NamesText = "fila" };
        Assert.Single(form.Ports);
        form.Ports[0].Port = "5672";
        form.Ports[0].Host = "fd00::5";
        form.Ports[0].TargetPort = "30672";
        form.AddPortCommand.Execute(null);

        form.SaveCommand.Execute(null);

        var port = Assert.Single(Assert.Single(_config.Current.Services).Ports);
        Assert.Equal((5672, "fd00::5", 30672), (port.Port, port.TargetHost, port.TargetPort));
    }

    [Fact]
    public void An_incomplete_port_says_what_is_missing()
    {
        var form = new ServiceEditorViewModel(_services, null) { NamesText = "fila" };
        form.Ports[0].Port = "5672";
        form.Ports[0].Host = "gateway.k8s";

        form.SaveCommand.Execute(null);

        Assert.Contains("incompleta", form.Error);
        Assert.Empty(_config.Current.Services);
    }

    [Fact]
    public void The_last_row_stays_when_removed()
    {
        var form = new ServiceEditorViewModel(_services, null);

        form.RemovePortCommand.Execute(form.Ports[0]);

        Assert.Single(form.Ports);
    }
}
