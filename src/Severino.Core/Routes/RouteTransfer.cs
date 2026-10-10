using System.Text.Json;
using Severino.Core.Configuration;

namespace Severino.Core.Routes;

/// <summary>
/// The backup file: what "Exportar" writes and "Importar" reads. Version 1 had only the routes;
/// version 2 also carries services and DNS entries.
/// </summary>
public sealed record RouteFile
{
    public const int CurrentVersion = 2;

    /// <summary>Format version; also tells a Severino routes file apart from any other JSON.</summary>
    public int Severino { get; set; }

    public IReadOnlyList<ExportedRoute> Routes { get; set; } = [];

    /// <summary>As saved here; an import gives them new ids and addresses.</summary>
    public IReadOnlyList<ServiceRoute> Services { get; set; } = [];

    /// <summary>As saved here; an import gives them new ids.</summary>
    public IReadOnlyList<DnsEntry> DnsEntries { get; set; } = [];
}

/// <summary>A route without its id: ids are per machine, and an import makes new ones.</summary>
public sealed record ExportedRoute
{
    public string Domain { get; set; } = "";
    public string Target { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public bool Https { get; set; }
    public bool RedirectToHttps { get; set; }
    public bool PreserveHost { get; set; }
    public bool IgnoreTargetCertErrors { get; set; }
    public string Notes { get; set; } = "";
    public string Path { get; set; } = "";
    public bool StripPath { get; set; }
    public string Group { get; set; } = "";
}

public sealed record InvalidImport(string Domain, string Reason);

/// <param name="Added">New routes, normalized and with fresh ids, not saved yet.</param>
/// <param name="Skipped">Domains already routed here (or twice in the file); left alone.</param>
public sealed record ImportResult(IReadOnlyList<RouteEntry> Added, IReadOnlyList<string> Skipped, IReadOnlyList<InvalidImport> Invalid);

public sealed class InvalidRouteFileException(string message, Exception? inner = null) : Exception(message, inner);

public static class RouteTransfer
{
    public static string Export(IEnumerable<RouteEntry> routes, IEnumerable<ServiceRoute>? services = null, IEnumerable<DnsEntry>? dns = null) => JsonSerializer.Serialize(
        new RouteFile
        {
            Severino = RouteFile.CurrentVersion,
            Services = [.. services ?? []],
            DnsEntries = [.. dns ?? []],
            Routes = [.. routes.Select(r => new ExportedRoute
            {
                Domain = r.Domain,
                Target = r.Target,
                Enabled = r.Enabled,
                Https = r.Https,
                RedirectToHttps = r.RedirectToHttps,
                PreserveHost = r.PreserveHost,
                IgnoreTargetCertErrors = r.IgnoreTargetCertErrors,
                Notes = r.Notes,
                Path = r.Path,
                StripPath = r.StripPath,
                Group = r.Group,
            })],
        },
        ConfigJsonContext.Default.RouteFile);

    /// <summary>
    /// Sorts the file's routes into new, already present and invalid, with the same rules as the
    /// route form. Existing routes are never replaced.
    /// </summary>
    /// <exception cref="InvalidRouteFileException">Not a Severino routes file, or one from a newer version.</exception>
    public static ImportResult Import(string json, IReadOnlyList<RouteEntry> existing, int httpPort, int? httpsPort, IReadOnlyList<ServiceRoute>? services = null) =>
        Import(Read(json), existing, httpPort, httpsPort, services);

    /// <summary>Reads a backup file of this version or an older one.</summary>
    /// <exception cref="InvalidRouteFileException">Not a Severino backup file, or one from a newer version.</exception>
    public static RouteFile Read(string json)
    {
        RouteFile? file;
        try
        {
            file = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.RouteFile);
        }
        catch (JsonException ex)
        {
            throw new InvalidRouteFileException("O arquivo não é um JSON válido.", ex);
        }
        if (file is null || file.Severino < 1)
            throw new InvalidRouteFileException("O arquivo não é uma exportação de rotas do Severino.");
        if (file.Severino > RouteFile.CurrentVersion)
            throw new InvalidRouteFileException("O arquivo veio de uma versão mais nova do Severino. Atualize antes de importar.");
        return file;
    }

    /// <summary>The routes of <paramref name="file"/>, sorted into new, already present and invalid.</summary>
    public static ImportResult Import(RouteFile file, IReadOnlyList<RouteEntry> existing, int httpPort, int? httpsPort, IReadOnlyList<ServiceRoute>? services = null)
    {
        var routes = existing.ToList();
        var added = new List<RouteEntry>();
        var skipped = new List<string>();
        var invalid = new List<InvalidImport>();
        foreach (var exported in file.Routes)
        {
            var route = new RouteEntry
            {
                Domain = exported.Domain ?? "",
                Target = exported.Target ?? "",
                Enabled = exported.Enabled,
                Https = exported.Https,
                RedirectToHttps = exported.Https && exported.RedirectToHttps,
                PreserveHost = exported.PreserveHost,
                IgnoreTargetCertErrors = exported.IgnoreTargetCertErrors,
                Notes = exported.Notes ?? "",
                Path = exported.Path ?? "",
                StripPath = exported.StripPath,
                Group = (exported.Group ?? "").Trim(),
            };

            var domain = RouteRules.Normalize(route.Domain);
            var path = RouteRules.NormalizePath(route.Path);
            if (domain is not null && routes.Any(r => RouteRules.Normalize(r.Domain) == domain && RouteRules.NormalizePath(r.Path) == path))
            {
                skipped.Add(domain + path);
                continue;
            }

            var errors = RouteRules.Validate(route, routes, httpPort, httpsPort, services);
            if (!errors.IsValid)
            {
                invalid.Add(new(route.Domain.Length > 0 ? route.Domain : "(sem domínio)", errors.Domain ?? errors.Path ?? errors.Target ?? ""));
                continue;
            }

            route = route with { Domain = domain!, Path = path! };
            routes.Add(route);
            added.Add(route);
        }
        return new(added, skipped, invalid);
    }
}
