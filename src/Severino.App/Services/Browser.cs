using System.Diagnostics;

namespace Severino.App.Services;

public static class Browser
{
    /// <summary>
    /// The address a route is reached at; the port only shows when it is not the scheme's default.
    /// A wildcard opens one example name below it; a path route opens its path.
    /// </summary>
    public static string UrlFor(string domain, int httpPort, int? httpsPort = null, string path = "") =>
        UrlForHost(domain.StartsWith("*.", StringComparison.Ordinal) ? "exemplo" + domain[1..] : domain, httpPort, httpsPort)
        + (path.Length > 0 ? path.TrimStart('/') : "");

    private static string UrlForHost(string domain, int httpPort, int? httpsPort) => httpsPort switch
    {
        443 => $"https://{domain}/",
        { } port => $"https://{domain}:{port}/",
        null when httpPort == 80 => $"http://{domain}/",
        _ => $"http://{domain}:{httpPort}/",
    };

    public static void Open(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
