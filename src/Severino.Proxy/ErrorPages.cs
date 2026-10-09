using System.Text.Encodings.Web;
using System.Text.Unicode;

namespace Severino.Proxy;

/// <summary>Self-contained HTML for the proxy's own responses, light and dark.</summary>
public static class ErrorPages
{
    // Unicode passes through; only markup characters are escaped.
    private static readonly HtmlEncoder Html = HtmlEncoder.Create(UnicodeRanges.All);

    public static string NotFound(string host, IEnumerable<string> domains, int port)
    {
        var links = domains.Select(d =>
        {
            var url = port == 80 ? $"http://{d}/" : $"http://{d}:{port}/";
            return $"<li><a href=\"{Html.Encode(url)}\">{Html.Encode(d)}</a></li>";
        }).ToList();

        var list = links.Count > 0
            ? $"<p>Rotas ativas:</p><ul>{string.Concat(links)}</ul>"
            : "<p>Nenhuma rota ativa no momento.</p>";

        return Page("Rota não encontrada", 404,
            $"<p>O Severino não tem rota para <strong>{Html.Encode(host)}</strong>.</p>{list}");
    }

    public static string BadGateway(string domain, string target) =>
        Page("Destino fora do ar", 502,
            $"<p><strong>{Html.Encode(domain)}</strong> → <code>{Html.Encode(Display(target))}</code> não respondeu. Seu servidor está rodando?</p>");

    public static string GatewayTimeout(string domain, string target) =>
        Page("Destino demorou demais", 504,
            $"<p><strong>{Html.Encode(domain)}</strong> → <code>{Html.Encode(Display(target))}</code> não respondeu em {ProxyConfigMapper.ActivityTimeout.TotalMinutes:0} minutos.</p>");

    private static string Display(string target) =>
        Uri.TryCreate(target, UriKind.Absolute, out var uri) ? uri.Authority : target;

    private static string Page(string title, int status, string body) => $$"""
        <!doctype html>
        <html lang="pt-BR">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>{{status}} · {{Html.Encode(title)}} · Severino</title>
        <style>
          :root { color-scheme: light dark; --bg: #f7f7f8; --fg: #1b1b1f; --muted: #5d5d66; --accent: #0f6cbd; --card: #ffffff; }
          @media (prefers-color-scheme: dark) { :root { --bg: #1c1c1e; --fg: #f2f2f4; --muted: #a6a6ad; --accent: #62a8f0; --card: #2a2a2d; } }
          body { margin: 0; min-height: 100vh; display: grid; place-items: center; background: var(--bg); color: var(--fg);
                 font: 16px/1.5 "Segoe UI Variable", "Segoe UI", system-ui, sans-serif; }
          main { max-width: 560px; margin: 16px; padding: 32px; background: var(--card); border-radius: 12px; }
          .status { color: var(--muted); font-size: 14px; letter-spacing: .04em; }
          h1 { margin: 4px 0 16px; font-size: 24px; font-weight: 600; }
          a { color: var(--accent); }
          code { font-family: "Cascadia Code", Consolas, monospace; }
          footer { margin-top: 24px; color: var(--muted); font-size: 13px; }
        </style>
        </head>
        <body>
        <main>
          <div class="status">{{status}}</div>
          <h1>{{Html.Encode(title)}}</h1>
          {{body}}
          <footer>Severino</footer>
        </main>
        </body>
        </html>
        """;
}
