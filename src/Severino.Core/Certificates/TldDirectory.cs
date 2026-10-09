using Severino.Core.Configuration;
using Severino.Core.Domains;

namespace Severino.Core.Certificates;

/// <summary>
/// Whether a TLD exists on the internet, asked once and remembered in config.json, so the CA's
/// coverage does not change from one run to the next.
/// </summary>
public sealed class TldDirectory(ConfigService config, IDnsResolver dns)
{
    public bool? Known(string tld) =>
        config.Current.State.TldExists.TryGetValue(tld, out var exists) ? exists : null;

    /// <summary>Looks up every TLD not seen before; failures stay unknown and are retried next time.</summary>
    public async Task ResolveAsync(IEnumerable<string> domains, CancellationToken cancellationToken)
    {
        var missing = domains
            .Select(CaCoverage.Tld)
            .Distinct(StringComparer.Ordinal)
            .Where(tld => !CaCoverage.ReservedTlds.Contains(tld) && Known(tld) is null)
            .ToList();

        foreach (var tld in missing)
        {
            if (await dns.TldExistsAsync(tld, cancellationToken) is { } exists)
                config.Update(c => c with
                {
                    State = c.State with { TldExists = new Dictionary<string, bool>(c.State.TldExists) { [tld] = exists } },
                });
        }
    }

    /// <summary>The coverage for these domains, after looking up their TLDs.</summary>
    public async Task<IReadOnlyList<string>> CoverageAsync(IReadOnlyList<string> domains, CancellationToken cancellationToken)
    {
        await ResolveAsync(domains, cancellationToken);
        return CaCoverage.Compute(domains, Known);
    }
}
