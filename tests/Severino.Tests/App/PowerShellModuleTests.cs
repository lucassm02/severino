using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Severino.App.Services;
using Severino.Core.Configuration;
using Severino.Core.Control;
using Severino.Core.Dns;
using Severino.Core.Routes;

namespace Severino.Tests.App;

/// <summary>The control handler on its own, and the real module in Windows PowerShell against a real pipe.</summary>
public sealed class PowerShellModuleTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "severino-tests", Guid.NewGuid().ToString("N"));
    private readonly string _pipe = $"Severino.Test.Control.{Guid.NewGuid():N}";
    private ConfigService _config = null!;
    private ControlHandler _handler = null!;
    private ControlServer _server = null!;

    public Task InitializeAsync()
    {
        _config = new ConfigService(new ConfigStore(Path.Combine(_dir, "config")));
        _config.Load();
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "hosts"), "10.0.0.8 sql.interno\r\n");
        var external = new ExternalHosts(Path.Combine(_dir, "hosts"));
        _handler = new ControlHandler(new RouteService(_config, external), new DnsService(_config, external, new NoHelper()), new ServiceRouteService(_config, external), external);
        _server = new ControlServer(_handler, NullLogger<ControlServer>.Instance, _pipe);
        _server.Start();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private JsonNode Send(string command, JsonObject? args = null) =>
        JsonNode.Parse(_handler.Handle(new JsonObject { ["command"] = command, ["args"] = args ?? [] }.ToJsonString()))!;

    [Fact]
    public void The_handler_uses_the_same_rules_as_the_screens()
    {
        Assert.True(Send("routes.add", new JsonObject { ["domain"] = "Meuapp.sev", ["target"] = "http://localhost:3000", ["group"] = "meuapp" })["ok"]!.GetValue<bool>());
        var duplicate = Send("routes.add", new JsonObject { ["domain"] = "meuapp.sev", ["target"] = "http://localhost:4000" });
        Assert.Equal("Já existe uma rota para este domínio.", duplicate["error"]!.GetValue<string>());
        Assert.StartsWith("sql.interno já está no hosts", Send("dns.set", new JsonObject { ["names"] = new JsonArray("sql.interno"), ["address"] = "10.0.0.9" })["error"]!.GetValue<string>());

        Assert.True(Send("routes.set-enabled", new JsonObject { ["group"] = "meuapp", ["enabled"] = false })["ok"]!.GetValue<bool>());
        Assert.False(_config.Current.Routes[0].Enabled);
        Assert.Equal("Pedido inválido.", JsonNode.Parse(_handler.Handle("nada"))!["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_module_creates_a_route_and_an_entry_through_the_pipe()
    {
        var module = Path.Combine(Root(), "powershell", "Severino", "Severino.psd1");
        var script =
            $"Import-Module '{module}'; " +
            "New-SeverinoRoute api.meuapp.sev http://localhost:8080 -Path /v1 -Group meuapp | Out-Null; " +
            "Set-SeverinoDns gateway.k8s, gw 192.168.203.100 | Out-Null; " +
            "$r = Get-SeverinoRoute api.meuapp.sev; $d = Get-SeverinoDns; $o = Get-SeverinoDns -Outside; " +
            "\"$($r.domain)$($r.path)|$($r.group)|$($d.names -join ',')=$($d.address)|$($o.names -join ',')\"; " +
            "try { Remove-SeverinoDns nao.existe -Confirm:$false } catch { \"erro: $($_.Exception.Message)\" }";

        var output = await RunPowerShellAsync(script);

        Assert.Contains("api.meuapp.sev/v1|meuapp|gateway.k8s,gw=192.168.203.100|sql.interno", output);
        Assert.Contains("erro: Nenhuma entrada DNS do Severino com nao.existe.", output);
        Assert.Equal("gateway.k8s", Assert.Single(_config.Current.DnsEntries).Names[0]);
    }

    private async Task<string> RunPowerShellAsync(string script)
    {
        var start = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command",
                     "[Console]::OutputEncoding = [Text.Encoding]::UTF8; " + script })
            start.ArgumentList.Add(argument);
        start.Environment["SEVERINO_CONTROL_PIPE"] = _pipe;
        using var process = Process.Start(start)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return output + error;
    }

    /// <summary>The repository, from this file's own path: the build output may live anywhere.</summary>
    private static string Root([System.Runtime.CompilerServices.CallerFilePath] string file = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "..", "..", ".."));

    private sealed class NoHelper : Severino.Core.Helper.IHelperClient
    {
        public Task<Severino.Contracts.HelperResponse> SendAsync(Severino.Contracts.HelperRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(Severino.Contracts.HelperResponse.Success("test"));
    }
}
