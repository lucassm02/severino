using Severino.Core.Configuration;
using Severino.Core.Routes;

namespace Severino.App.ViewModels;

/// <summary>
/// What a service opens in the browser: its HTTP ports (https:// when its HTTPS holds) and the
/// other ports that are not a database or a queue. 443 without the HTTP mark is the service's
/// own TLS, so it opens https://.
/// </summary>
public static class ServiceLinks
{
    public static IReadOnlyList<string> For(ServiceRoute service, bool httpsActive)
    {
        if (service.Names.Count == 0)
            return [];
        var name = service.Names[0];
        var https = httpsActive && service.Https && ServiceHttp.HasHttp(service);
        var main = ServiceHttp.Main(service);
        var tls = ServiceHttp.ExtraTlsPort(service);
        var links = new List<string>();
        foreach (var port in service.Ports)
        {
            var url = port switch
            {
                { Http: true } when https => port == main && tls is not null ? $"https://{name}/" : $"https://{name}:{port.Port}/",
                { Http: true } => Plain(name, port.Port),
                { Port: 443 } => $"https://{name}/",
                _ when KnownPorts.NonHttpProgram(port.Port) is null => Plain(name, port.Port),
                _ => null,
            };
            if (url is not null && !links.Contains(url))
                links.Add(url);
        }
        return links;
    }

    private static string Plain(string name, int port) => port == 80 ? $"http://{name}/" : $"http://{name}:{port}/";
}
