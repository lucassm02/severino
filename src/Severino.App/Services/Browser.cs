using System.Diagnostics;

namespace Severino.App.Services;

public static class Browser
{
    /// <summary>The address a route is reached at; the port only shows when it is not 80.</summary>
    public static string UrlFor(string domain, int httpPort) =>
        httpPort == 80 ? $"http://{domain}/" : $"http://{domain}:{httpPort}/";

    public static void Open(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
