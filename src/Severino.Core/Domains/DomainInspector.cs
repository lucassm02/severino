namespace Severino.Core.Domains;

public enum DomainWarningKind
{
    ExistsOnInternet,
    HstsPreload,
    Mdns,
    LookupFailed,
}

public sealed record DomainWarning(DomainWarningKind Kind, string Message)
{
    // Screen readers and UI Automation read list items through ToString.
    public override string ToString() => Message;
}

/// <summary>Non-blocking warnings about a domain the user is about to route.</summary>
public sealed class DomainInspector(IDnsResolver dns)
{
    // TLDs preloaded as a whole in Chromium's HSTS list (snapshot; most are Google's). Names under
    // them only open over HTTPS.
    private static readonly HashSet<string> HstsPreloadedTlds = new(StringComparer.Ordinal)
    {
        "android", "app", "bank", "boo", "channel", "chrome", "dad", "day", "dev", "eat", "esq",
        "fly", "foo", "gle", "gmail", "google", "hangout", "ing", "insurance", "meet", "meme",
        "mov", "new", "nexus", "page", "phd", "play", "prof", "rsvp", "search", "youtube", "zip",
    };

    /// <summary>Warnings that need no network. <paramref name="domain"/> must be normalized.</summary>
    public IReadOnlyList<DomainWarning> CheckLocal(string domain)
    {
        var tld = domain[(domain.LastIndexOf('.') + 1)..];
        var warnings = new List<DomainWarning>();

        if (HstsPreloadedTlds.Contains(tld))
            warnings.Add(new(DomainWarningKind.HstsPreload,
                $"Domínios .{tld} só abrem com HTTPS nos navegadores (HSTS preload). Esta rota vai funcionar quando o HTTPS chegar ao Severino."));

        if (tld == "local")
            warnings.Add(new(DomainWarningKind.Mdns,
                ".local é usado pelo mDNS e costuma deixar a resolução lenta. Prefira .sev, .test ou .localhost."));

        return warnings;
    }

    /// <summary>Asks the public DNS whether the name already exists. <paramref name="domain"/> must be normalized.</summary>
    public async Task<DomainWarning?> CheckInternetAsync(string domain, CancellationToken cancellationToken)
    {
        var result = await dns.LookupAsync(domain, cancellationToken);
        return result.Outcome switch
        {
            DnsLookupOutcome.Exists => new(DomainWarningKind.ExistsOnInternet,
                $"{domain} existe na internet (resolve para {result.Address}). Enquanto esta rota estiver ativa, esta máquina não acessa o site real."),
            DnsLookupOutcome.Failed => new(DomainWarningKind.LookupFailed,
                "Não foi possível verificar se este domínio existe na internet."),
            _ => null,
        };
    }
}
