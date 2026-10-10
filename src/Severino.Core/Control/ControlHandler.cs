using System.Text.Json;
using System.Text.Json.Nodes;
using Severino.Core.Configuration;
using Severino.Core.Dns;
using Severino.Core.Routes;

namespace Severino.Core.Control;

/// <summary>
/// The commands of the PowerShell module, one JSON request in, one JSON response out:
/// <c>{"command": "routes.add", "args": {...}}</c> → <c>{"ok": true, "data": ...}</c>. Every change
/// goes through the same services and validations as the app's own screens.
/// </summary>
public sealed class ControlHandler(RouteService routes, DnsService dns, ServiceRouteService services, ExternalHosts? external = null)
{
    public const int MaxMessageBytes = 256 * 1024;

    public string Handle(string json)
    {
        string command;
        JsonObject args;
        try
        {
            var request = JsonNode.Parse(json) as JsonObject ?? throw new JsonException();
            command = request["command"]?.GetValue<string>() ?? throw new JsonException();
            args = request["args"] as JsonObject ?? [];
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return Fail("Pedido inválido.");
        }

        try
        {
            return command switch
            {
                "routes.list" => Ok(new JsonArray([.. routes.Routes.Select(Route)])),
                "routes.add" => AddRoute(args),
                "routes.set-enabled" => SetRouteEnabled(args),
                "routes.remove" => RemoveRoute(args),
                "dns.list" => Ok(DnsList()),
                "dns.set" => SetDns(args),
                "dns.remove" => RemoveDns(args),
                "services.list" => Ok(new JsonArray([.. services.Services.Select(Service)])),
                _ => Fail($"Comando desconhecido: {command}"),
            };
        }
        catch (ArgumentException ex)
        {
            // The message without the " (Parameter 'route')" .NET adds.
            return Fail(ex.ParamName is { } param ? ex.Message.Replace($" (Parameter '{param}')", "") : ex.Message);
        }
    }

    private string AddRoute(JsonObject args)
    {
        var https = Bool(args, "https");
        var saved = routes.Save(new RouteEntry
        {
            Domain = Text(args, "domain"),
            Target = Text(args, "target"),
            Path = Text(args, "path"),
            Https = https,
            RedirectToHttps = https && Bool(args, "redirect", true),
            Group = Text(args, "group"),
            Notes = Text(args, "notes"),
        });
        return Ok(Route(saved));
    }

    private string SetRouteEnabled(JsonObject args)
    {
        var enabled = Bool(args, "enabled", true);
        if (Text(args, "group") is { Length: > 0 } group)
        {
            if (!routes.Groups.Contains(group))
                return Fail($"Nenhuma rota no grupo {group}.");
            routes.SetGroupEnabled(group, enabled);
            return Ok(new JsonArray([.. routes.Routes.Where(r => r.Group == group).Select(Route)]));
        }
        var route = FindRoute(args);
        routes.SetEnabled(route.Id, enabled);
        return Ok(Route(routes.Routes.First(r => r.Id == route.Id)));
    }

    private string RemoveRoute(JsonObject args)
    {
        var route = FindRoute(args);
        routes.Remove(route.Id);
        return Ok(Route(route));
    }

    private RouteEntry FindRoute(JsonObject args)
    {
        var domain = RouteRules.Normalize(Text(args, "domain")) ?? throw new ArgumentException("Informe o domínio da rota.");
        var path = RouteRules.NormalizePath(Text(args, "path")) ?? throw new ArgumentException("Caminho inválido.");
        return routes.Routes.FirstOrDefault(r => r.Domain == domain && r.Path == path)
            ?? throw new ArgumentException($"Nenhuma rota para {domain}{path}.");
    }

    private JsonObject DnsList() => new()
    {
        ["entries"] = new JsonArray([.. dns.Entries.Select(e => new JsonObject
        {
            ["id"] = e.Id.ToString(),
            ["names"] = new JsonArray([.. e.Names.Select(n => JsonValue.Create(n))]),
            ["address"] = e.Address,
            ["enabled"] = e.Enabled,
            ["notes"] = e.Notes,
        })]),
        ["outside"] = new JsonArray([.. (external?.Lines ?? []).Select(l => new JsonObject
        {
            ["names"] = new JsonArray([.. l.Names.Select(n => JsonValue.Create(n))]),
            ["address"] = l.Address,
            ["origin"] = l.Origin,
            ["removed"] = l.Removed,
        })]),
    };

    /// <summary>Creates the entry, or updates the one that already has the first name.</summary>
    private string SetDns(JsonObject args)
    {
        var names = (args["names"] as JsonArray)?.Select(n => n?.GetValue<string>() ?? "").Where(n => n.Length > 0).ToList() ?? [];
        if (names.Count == 0)
            return Fail("Informe pelo menos um nome.");
        var first = names[0].Trim().TrimEnd('.').ToLowerInvariant();
        var existing = dns.Entries.FirstOrDefault(e => e.Names.Contains(first));
        var saved = dns.Save((existing ?? new DnsEntry()) with
        {
            Names = names,
            Address = Text(args, "address"),
            Enabled = Bool(args, "enabled", true),
            Notes = args.ContainsKey("notes") ? Text(args, "notes") : existing?.Notes ?? "",
        });
        return Ok(new JsonObject
        {
            ["id"] = saved.Id.ToString(),
            ["names"] = new JsonArray([.. saved.Names.Select(n => JsonValue.Create(n))]),
            ["address"] = saved.Address,
            ["enabled"] = saved.Enabled,
        });
    }

    private string RemoveDns(JsonObject args)
    {
        var name = Text(args, "name").Trim().TrimEnd('.').ToLowerInvariant();
        var entry = dns.Entries.FirstOrDefault(e => e.Names.Contains(name)) ?? throw new ArgumentException($"Nenhuma entrada DNS do Severino com {name}.");
        dns.Remove(entry.Id);
        return Ok(new JsonObject { ["names"] = new JsonArray([.. entry.Names.Select(n => JsonValue.Create(n))]), ["address"] = entry.Address });
    }

    private static JsonObject Route(RouteEntry r) => new()
    {
        ["id"] = r.Id.ToString(),
        ["domain"] = r.Domain,
        ["path"] = r.Path,
        ["target"] = r.Target,
        ["enabled"] = r.Enabled,
        ["https"] = r.Https,
        ["group"] = r.Group,
    };

    private static JsonObject Service(ServiceRoute s) => new()
    {
        ["id"] = s.Id.ToString(),
        ["names"] = new JsonArray([.. s.Names.Select(n => JsonValue.Create(n))]),
        ["address"] = s.Address,
        ["ports"] = new JsonArray([.. s.Ports.Select(p => JsonValue.Create(p.ToString()))]),
        ["enabled"] = s.Enabled,
        ["portForward"] = s.PortForward,
        ["origin"] = s.Origin is { } o ? $"{o.Kind} {o.Source} {o.Context} {o.Namespace}/{o.Name}".Trim() : null,
    };

    private static string Text(JsonObject args, string name) =>
        args[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";

    private static bool Bool(JsonObject args, string name, bool fallback = false) =>
        args[name] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : fallback;

    private static string Ok(JsonNode data) => new JsonObject { ["ok"] = true, ["data"] = data }.ToJsonString();

    private static string Fail(string error) => new JsonObject { ["ok"] = false, ["error"] = error }.ToJsonString();
}
