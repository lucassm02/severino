using System.IO;
using System.Security.Cryptography.X509Certificates;
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
    /// <summary>The user stopped at the app's own explanation, before Windows' warning.</summary>
    Cancelled,
    /// <summary>There is no route to cover, so a CA would have no constraints.</summary>
    NoRoutes,
}

/// <summary>What Windows' warning is about to show, so the user can check it is this CA.</summary>
/// <param name="Name">The name Windows says the CA "claims to represent".</param>
/// <param name="Thumbprint">SHA-1, in groups of 8 as Windows prints it.</param>
/// <param name="Names">What the CA can sign for.</param>
/// <param name="ReplacesCurrent">A root is already trusted, and Windows will also ask to remove it.</param>
public sealed record TrustPrompt(string Name, string Thumbprint, IReadOnlyList<string> Names, bool ReplacesCurrent);

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
    /// works over HTTP today changes behaviour. <paramref name="confirm"/> runs before Windows' warning.
    /// </summary>
    public async Task<HttpsActionResult> ActivateAsync(Func<TrustPrompt, Task<bool>> confirm, CancellationToken cancellationToken = default)
    {
        var routes = config.Current.Routes;
        if (routes.Count == 0)
            return HttpsActionResult.NoRoutes;

        var domains = CaCoverage.RequiredDomains(routes.Select(r => r with { Https = true }));
        var result = await IssueAsync(domains, confirm, cancellationToken);
        if (result is HttpsActionResult.Done or HttpsActionResult.DoneOldRootKept)
            config.Update(c => c with { Routes = [.. c.Routes.Select(r => r.Https ? r : r with { Https = true })] });
        return result;
    }

    /// <summary>
    /// Replaces the CA with one covering exactly the HTTPS routes of today: adds new names and
    /// drops removed ones. <paramref name="confirm"/> runs before Windows' warning.
    /// </summary>
    public Task<HttpsActionResult> ReissueAsync(Func<TrustPrompt, Task<bool>> confirm, CancellationToken cancellationToken = default)
    {
        var domains = CaCoverage.RequiredDomains(config.Current.Routes);
        return domains.Count == 0
            ? Task.FromResult(HttpsActionResult.NoRoutes)
            : IssueAsync(domains, confirm, cancellationToken);
    }

    /// <summary>Untrusts the root and deletes every key. False when the user kept the root in Windows.</summary>
    public Task<bool> RemoveAsync() => Task.FromResult(WindowsPrompt.Run(ca.Remove));

    /// <summary>"qualquer nome .sev, api.empresa.com": a whole TLD reads as such.</summary>
    public static string DescribeNames(IEnumerable<string> names) =>
        string.Join(", ", names.Select(n => n.Contains('.') ? n : "qualquer nome ." + n));

    /// <summary>Writes the root, without its key, as PEM.</summary>
    public void ExportPem(string path)
    {
        var pem = ca.ExportPem() ?? throw new InvalidOperationException("O HTTPS não está ativo.");
        File.WriteAllText(path, pem);
    }

    private async Task<HttpsActionResult> IssueAsync(IReadOnlyList<string> domains, Func<TrustPrompt, Task<bool>> confirm, CancellationToken cancellationToken)
    {
        var names = await tlds.CoverageAsync(domains, cancellationToken);
        var created = ca.Prepare(names);
        var prompt = new TrustPrompt(
            created.Certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false),
            string.Join(' ', created.Thumbprint.Chunk(8).Select(chunk => new string(chunk))),
            created.Names,
            ReplacesCurrent: IsActive);
        if (!await confirm(prompt))
        {
            created.Dispose();
            return HttpsActionResult.Cancelled;
        }

        return WindowsPrompt.Run(() => ca.Activate(created)) switch
        {
            ActivationResult.Activated => HttpsActionResult.Done,
            ActivationResult.ActivatedOldRootKept => HttpsActionResult.DoneOldRootKept,
            _ => HttpsActionResult.Declined,
        };
    }
}
