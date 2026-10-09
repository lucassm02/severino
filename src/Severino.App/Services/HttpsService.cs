using System.IO;
using Severino.Core.Certificates;
using Severino.Core.Configuration;
using Severino.Core.Routes;
using Severino.Proxy.Certificates;

namespace Severino.App.Services;

public enum HttpsActionResult
{
    Done,
    /// <summary>Done, but Windows kept the previous root because the user said no; it is harmless.</summary>
    DoneOldRootKept,
    /// <summary>The user declined Windows' warning; nothing changed.</summary>
    Declined,
    /// <summary>There is no route to cover, so a CA would have no constraints.</summary>
    NoRoutes,
}

/// <summary>
/// The HTTPS actions behind the UI: activating, reissuing and removing the local CA, keeping the
/// routes' HTTPS flags and the CA's coverage in step.
/// </summary>
public sealed class HttpsService(ConfigService config, LocalCa ca, TldDirectory tlds)
{
    public LocalCaStatus Status => ca.Status;

    public bool IsActive => ca.Status.State == LocalCaState.Active;

    /// <summary>Raised on the thread that made the change.</summary>
    public event EventHandler<LocalCaStatus>? Changed
    {
        add => ca.Changed += value;
        remove => ca.Changed -= value;
    }

    /// <summary>True when the current CA can sign for <paramref name="domain"/>.</summary>
    public bool Covers(string domain) => RouteRules.Normalize(domain) is { } normalized && ca.Covers(normalized);

    /// <summary>Domains of HTTPS routes the current CA cannot sign for.</summary>
    public IReadOnlyList<string> Uncovered() =>
        IsActive ? [.. CaCoverage.RequiredDomains(config.Current.Routes).Where(d => !ca.Covers(d))] : [];

    /// <summary>
    /// Creates the CA and turns HTTPS on for every route, without redirecting, so nothing that
    /// works over HTTP today changes behaviour. Shows Windows' warning.
    /// </summary>
    public async Task<HttpsActionResult> ActivateAsync(CancellationToken cancellationToken = default)
    {
        var routes = config.Current.Routes;
        if (routes.Count == 0)
            return HttpsActionResult.NoRoutes;

        var domains = CaCoverage.RequiredDomains(routes.Select(r => r with { Https = true }));
        var result = await IssueAsync(domains, cancellationToken);
        if (result is HttpsActionResult.Done or HttpsActionResult.DoneOldRootKept)
            config.Update(c => c with { Routes = [.. c.Routes.Select(r => r.Https ? r : r with { Https = true })] });
        return result;
    }

    /// <summary>
    /// Replaces the CA with one covering exactly the HTTPS routes of today: adds new names and
    /// drops removed ones. Shows Windows' warning.
    /// </summary>
    public Task<HttpsActionResult> ReissueAsync(CancellationToken cancellationToken = default)
    {
        var domains = CaCoverage.RequiredDomains(config.Current.Routes);
        return domains.Count == 0
            ? Task.FromResult(HttpsActionResult.NoRoutes)
            : IssueAsync(domains, cancellationToken);
    }

    /// <summary>Untrusts the root and deletes every key. False when the user kept the root in Windows.</summary>
    public Task<bool> RemoveAsync() => Task.Run(ca.Remove);

    /// <summary>Writes the root, without its key, as PEM.</summary>
    public void ExportPem(string path)
    {
        var pem = ca.ExportPem() ?? throw new InvalidOperationException("O HTTPS não está ativo.");
        File.WriteAllText(path, pem);
    }

    private async Task<HttpsActionResult> IssueAsync(IReadOnlyList<string> domains, CancellationToken cancellationToken)
    {
        var names = await tlds.CoverageAsync(domains, cancellationToken);
        // Windows' warning blocks the calling thread until the user answers.
        var result = await Task.Run(() => ca.Activate(names), cancellationToken);
        return result switch
        {
            ActivationResult.Activated => HttpsActionResult.Done,
            ActivationResult.ActivatedOldRootKept => HttpsActionResult.DoneOldRootKept,
            _ => HttpsActionResult.Declined,
        };
    }
}
