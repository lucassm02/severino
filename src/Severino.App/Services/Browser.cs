using System.Diagnostics;

namespace Severino.App.Services;

public static class Browser
{
    /// <summary>The address a route is reached at; the port only shows when it is not the scheme's default.</summary>
    public static string UrlFor(string domain, int httpPort, int? httpsPort = null) => httpsPort switch
    {
        443 => $"https://{domain}/",
        { } port => $"https://{domain}:{port}/",
        null when httpPort == 80 => $"http://{domain}/",
        _ => $"http://{domain}:{httpPort}/",
    };

    public static void Open(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
