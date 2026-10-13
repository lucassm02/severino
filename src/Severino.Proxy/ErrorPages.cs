using System.Text.Encodings.Web;
using System.Text.Unicode;
using Severino.Proxy.Resources;

namespace Severino.Proxy;

/// <summary>
/// Self-contained HTML for the proxy's own responses, light and dark: a doorman's badge with the
/// mascot, inlined so the page needs nothing else from the proxy.
/// </summary>
public static class ErrorPages
{
    // Unicode passes through; only markup characters are escaped.
    private static readonly HtmlEncoder Html = HtmlEncoder.Create(UnicodeRanges.All);

    private static readonly Lazy<string> Face = new(() =>
    {
        using var stream = typeof(ErrorPages).Assembly.GetManifestResourceStream("Severino.Proxy.severino-192.png")!;
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return "data:image/png;base64," + Convert.ToBase64String(buffer.ToArray());
    });

    /// <param name="routeDisabled">The host has a route, switched off.</param>
    public static string NotFound(string host, IEnumerable<string> domains, string scheme, int port, bool routeDisabled = false)
    {
        var defaultPort = scheme == "https" ? 443 : 80;
        var links = domains.Select(d =>
        {
            var url = port == defaultPort ? $"{scheme}://{d}/" : $"{scheme}://{d}:{port}/";
            return $"<li><a href=\"{Html.Encode(url)}\"><span>{Html.Encode(d)}</span><span aria-hidden=\"true\">→</span></a></li>";
        }).ToList();

        var list = links.Count > 0
            ? $"<section><h2>{Html.Encode(Strings.Error_ActiveRoutes)}</h2><ul>{string.Concat(links)}</ul></section>"
            : $"<p class=\"hint\">{Html.Encode(Strings.Error_NoActiveRoutes)}</p>";
        var name = $"<strong>{Html.Encode(host)}</strong>";

        return routeDisabled
            ? Page(Strings.Error_DisabledTitle, 404,
                $"<p class=\"lead\">{Format(Strings.Error_DisabledLead, name)}</p><p>{Html.Encode(Strings.Error_DisabledText)}</p>{list}")
            : Page(Strings.Error_NotFoundTitle, 404,
                $"<p class=\"lead\">{Format(Strings.Error_NotFoundLead, name)}</p><p>{Html.Encode(Strings.Error_NotFoundText)}</p>{list}");
    }

    public static string BadGateway(string domain, string target) =>
        Page(Strings.Error_BadGatewayTitle, 502,
            $"<p class=\"lead\">{Format(Strings.Error_BadGatewayLead, $"<strong>{Html.Encode(domain)}</strong>", $"<code>{Html.Encode(Display(target))}</code>")}</p>" +
            $"<p>{Html.Encode(Strings.Error_BadGatewayText)}</p>");

    public static string GatewayTimeout(string domain, string target) =>
        Page(Strings.Error_TimeoutTitle, 504,
            $"<p class=\"lead\">{Format(Strings.Error_TimeoutLead, $"<strong>{Html.Encode(domain)}</strong>", $"<code>{Html.Encode(Display(target))}</code>", ProxyConfigMapper.ActivityTimeout.TotalMinutes.ToString("0"))}</p>" +
            $"<p>{Html.Encode(Strings.Error_TimeoutText)}</p>");

    public static string FolderMissing(string domain, string folder) =>
        Page(Strings.Error_FolderMissingTitle, 502,
            $"<p class=\"lead\">{Format(Strings.Error_FolderMissingLead, $"<strong>{Html.Encode(domain)}</strong>", $"<code>{Html.Encode(folder)}</code>")}</p>" +
            $"<p>{Html.Encode(Strings.Error_FolderMissingText)}</p>");

    public static string FileNotFound(string domain, string path, string folder) =>
        Page(Strings.Error_FileNotFoundTitle, 404,
            $"<p class=\"lead\">{Format(Strings.Error_FileNotFoundLead, $"<code>{Html.Encode(path)}</code>", $"<code>{Html.Encode(folder)}</code>")}</p>" +
            $"<p>{Html.Encode(string.Format(Strings.Error_FileNotFoundText, domain))}</p>");

    /// <summary>A failure the route asked for: it says so, so nobody hunts a bug that is not there.</summary>
    public static string Simulated(string domain, int status, int percent) =>
        Page(Strings.Error_SimulatedTitle, status,
            $"<p class=\"lead\">{Format(Strings.Error_SimulatedLead, $"<strong>{Html.Encode(domain)}</strong>", status, percent)}</p>" +
            $"<p>{Html.Encode(Strings.Error_SimulatedText)}</p>");

    /// <summary>Severino's own login page for a route with <see cref="Severino.Core.Configuration.AccessMode.Login"/>.</summary>
    /// <param name="action">Where the form posts: the route's login path.</param>
    /// <param name="next">The path to go back to, already checked.</param>
    public static string Login(string domain, string action, string next, string? user, bool failed) =>
        Page(Strings.Access_LoginTitle, 401,
            $"<p class=\"lead\">{Format(Strings.Access_LoginLead, $"<strong>{Html.Encode(domain)}</strong>")}</p>" +
            $"<p>{Html.Encode(Strings.Access_LoginText)}</p>" +
            (failed ? $"<p class=\"error\" role=\"alert\">{Html.Encode(Strings.Access_Wrong)}</p>" : "") +
            $"<form method=\"post\" action=\"{Html.Encode(action)}\">" +
            $"<input type=\"hidden\" name=\"next\" value=\"{Html.Encode(next)}\">" +
            $"<label>{Html.Encode(Strings.Access_User)}<input name=\"user\" autocomplete=\"username\" required value=\"{Html.Encode(user ?? "")}\"{(user is null ? " autofocus" : "")}></label>" +
            $"<label>{Html.Encode(Strings.Access_Password)}<input name=\"password\" type=\"password\" autocomplete=\"current-password\" required{(user is null ? "" : " autofocus")}></label>" +
            $"<button type=\"submit\">{Html.Encode(Strings.Access_SignIn)}</button></form>",
            Strings.Access_LoginBadge);

    /// <summary>A request that is not a page, to a route with the login page: where to sign in.</summary>
    public static string LoginNeeded(string domain, string url) =>
        Page(Strings.Access_RequiredTitle, 401,
            $"<p class=\"lead\">{Format(Strings.Access_LoginLead, $"<strong>{Html.Encode(domain)}</strong>")}</p>" +
            $"<p><a href=\"{Html.Encode(url)}\">{Html.Encode(Strings.Access_SignIn)}</a></p>");

    public static string BasicNeeded(string domain) =>
        Page(Strings.Access_RequiredTitle, 401,
            $"<p class=\"lead\">{Format(Strings.Access_BasicLead, $"<strong>{Html.Encode(domain)}</strong>")}</p>" +
            $"<p>{Html.Encode(Strings.Access_BasicText)}</p>");

    public static string HeaderNeeded(string domain, string header) =>
        Page(Strings.Access_RequiredTitle, 401,
            $"<p class=\"lead\">{Format(Strings.Access_HeaderLead, $"<strong>{Html.Encode(domain)}</strong>", $"<code>{Html.Encode(header)}</code>")}</p>" +
            $"<p>{Html.Encode(Strings.Access_HeaderText)}</p>");

    /// <summary>A resource sentence with markup already made for its placeholders; the sentence itself is encoded.</summary>
    private static string Format(string sentence, params object[] markup) => string.Format(Html.Encode(sentence), markup);

    private static string Display(string target) =>
        Uri.TryCreate(target, UriKind.Absolute, out var uri) ? uri.Authority : target;

    /// <param name="badge">What the orange badge says; the status by default.</param>
    private static string Page(string title, int status, string body, string? badge = null) => $$"""
        <!doctype html>
        <html lang="{{System.Globalization.CultureInfo.CurrentUICulture.Name}}">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>{{status}} · {{Html.Encode(title)}} · Severino</title>
        <link rel="icon" href="{{Face.Value}}">
        <style>
          :root {
            color-scheme: light dark;
            --bg: #eef2f8; --card: #ffffff; --fg: #0f1b2d; --muted: #5b6578; --line: #dde3ec;
            --accent: #013166; --band-from: #011d41; --band-to: #013166; --orange: #ee8f3b; --on-orange: #011d41;
            --row: #f5f7fb; --row-hover: #e8eef8;
          }
          @media (prefers-color-scheme: dark) {
            :root {
              --bg: #0a1322; --card: #121d30; --fg: #eef2f8; --muted: #9aa6ba; --line: #23314a;
              --accent: #5b9bef; --band-from: #011d41; --band-to: #0b3a75; --orange: #f2a25c;
              --row: #172540; --row-hover: #1d2e50;
            }
          }
          * { box-sizing: border-box; }
          body {
            margin: 0; min-height: 100vh; display: grid; place-items: center; padding: 24px 16px;
            background: radial-gradient(circle at 50% 0%, color-mix(in srgb, var(--accent) 12%, transparent), transparent 60%), var(--bg);
            color: var(--fg); font: 16px/1.55 "Segoe UI Variable Text", "Segoe UI", system-ui, sans-serif;
          }
          main {
            width: 100%; max-width: 520px; background: var(--card); border: 1px solid var(--line); border-radius: 20px;
            overflow: hidden; box-shadow: 0 1px 2px rgb(0 0 0 / .06), 0 16px 40px rgb(1 29 65 / .12);
          }
          .band {
            position: relative; height: 92px; padding: 16px 20px;
            background: linear-gradient(135deg, var(--band-from), var(--band-to));
            display: flex; justify-content: space-between; align-items: flex-start;
          }
          .band::after { content: ""; position: absolute; left: 0; right: 0; bottom: 0; height: 6px; background: #f6f1e7; opacity: .9; }
          .desk { color: #c9d6ea; font-size: 12px; font-weight: 600; letter-spacing: .14em; text-transform: uppercase; }
          .code {
            background: var(--orange); color: var(--on-orange); font-weight: 700; font-size: 14px; letter-spacing: .04em;
            padding: 2px 10px; border-radius: 999px;
          }
          .face {
            display: block; width: 104px; height: 104px; margin: -56px auto 0; position: relative;
            border-radius: 50%; background: #f6f1e7; padding: 6px; border: 3px solid var(--card);
            box-shadow: 0 0 0 3px var(--orange);
          }
          .content { padding: 12px 32px 28px; }
          h1 { margin: 12px 0 8px; font-size: 26px; line-height: 1.25; font-weight: 700; text-align: center; letter-spacing: -.01em; }
          .lead { font-size: 17px; text-align: center; margin: 0 0 6px; }
          .content > p:not(.lead) { color: var(--muted); text-align: center; margin: 0; }
          .hint { margin-top: 20px !important; }
          section { margin-top: 24px; }
          h2 { margin: 0 0 8px; color: var(--muted); font-size: 12px; font-weight: 600; letter-spacing: .1em; text-transform: uppercase; }
          ul { list-style: none; margin: 0; padding: 0; display: grid; gap: 6px; }
          li a {
            display: flex; justify-content: space-between; gap: 12px; padding: 10px 14px; border-radius: 10px;
            background: var(--row); color: var(--accent); font-weight: 600; text-decoration: none; overflow-wrap: anywhere;
          }
          li a:hover, li a:focus-visible { background: var(--row-hover); outline: none; }
          li a:focus-visible { box-shadow: 0 0 0 2px var(--accent); }
          code { font-family: "Cascadia Code", "Cascadia Mono", Consolas, monospace; font-size: .92em; padding: 1px 6px; border-radius: 6px; background: var(--row); }
          strong { overflow-wrap: anywhere; }
          footer {
            display: flex; flex-wrap: wrap; justify-content: center; align-items: baseline; gap: 2px 8px; padding: 14px 20px; text-align: center;
            border-top: 1px solid var(--line); color: var(--muted); font-size: 13px;
          }
          .brand { color: var(--fg); font-weight: 800; letter-spacing: -.01em; }
          .brand span { color: var(--orange); }
          form { display: grid; gap: 14px; margin-top: 20px; }
          label { display: grid; gap: 6px; font-size: 14px; font-weight: 600; }
          input {
            font: inherit; font-weight: 400; color: var(--fg); background: var(--row); border: 1px solid var(--line);
            border-radius: 10px; padding: 10px 12px;
          }
          input:focus-visible { outline: none; border-color: var(--accent); box-shadow: 0 0 0 2px color-mix(in srgb, var(--accent) 35%, transparent); }
          button {
            font: inherit; font-weight: 700; color: #fff; background: var(--accent); border: 0; border-radius: 10px;
            padding: 11px 16px; cursor: pointer; margin-top: 4px;
          }
          button:hover { filter: brightness(1.1); }
          button:focus-visible { outline: 2px solid var(--orange); outline-offset: 2px; }
          .error { color: #c42b1c !important; font-weight: 600; margin-top: 12px !important; }
          @media (prefers-color-scheme: dark) { .error { color: #ff99a4 !important; } button { color: #011d41; } }
        </style>
        </head>
        <body>
        <main>
          <div class="band">
            <span class="desk">{{Html.Encode(Strings.Error_FrontDesk)}}</span>
            <span class="code">{{Html.Encode(badge ?? status.ToString(System.Globalization.CultureInfo.InvariantCulture))}}</span>
          </div>
          <img class="face" src="{{Face.Value}}" alt="{{Html.Encode(Strings.Common_SeverinoDoorman)}}" width="104" height="104">
          <div class="content">
            <h1>{{Html.Encode(title)}}</h1>
            {{body}}
          </div>
          <footer><span class="brand">severino<span>.sev</span></span><span>{{Html.Encode(Strings.Error_Tagline)}}</span></footer>
        </main>
        </body>
        </html>
        """;
}
