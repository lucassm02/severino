using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Severino.Core.Configuration;
using Severino.Core.Routes;
using Severino.Proxy.Resources;

namespace Severino.Proxy;

/// <summary>A route's access, ready for the gate: the route's path prefix places its login page.</summary>
internal sealed record AccessRule(Guid RouteId, RouteAccess Access, string Prefix, bool Cors);

/// <summary>
/// Stops a request before forwarding when the route asks for a password or a header and the
/// request does not bring it. The login page and its session cookie are Severino's own: the
/// cookie is an expiry signed with a key of this machine, and the password's hash goes into the
/// signature, so changing the password ends every session.
/// </summary>
public sealed class RouteAccessGate(byte[] key, TimeProvider time)
{
    public const string LoginPath = "/.severino/login";
    public const string LogoutPath = "/.severino/logout";
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(12);

    /// <summary>What a wrong password waits before the answer; enough against guessing on loopback.</summary>
    private static readonly TimeSpan FailureDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>Basic Auth headers already checked: PBKDF2 is slow on purpose, and every asset brings the header.</summary>
    private readonly ConcurrentDictionary<string, bool> _checked = new();

    public static string CookieName(Guid routeId) => $"severino-{routeId:N}";

    /// <summary>True when the request may go on; otherwise the answer is already written.</summary>
    internal async Task<bool> AllowAsync(HttpContext context, AccessRule rule, string domain)
    {
        var request = context.Request;
        // Browsers never send credentials on a preflight: with CORS on, it is the rules' to answer.
        if (rule.Cors && HttpMethods.IsOptions(request.Method) && request.Headers.ContainsKey("Access-Control-Request-Method"))
            return true;

        return rule.Access.Mode switch
        {
            AccessMode.Login => await LoginAsync(context, rule, domain),
            AccessMode.Basic => await BasicAsync(context, rule, domain),
            AccessMode.Header => await HeaderAsync(context, rule, domain),
            _ => true,
        };
    }

    private async Task<bool> LoginAsync(HttpContext context, AccessRule rule, string domain)
    {
        var request = context.Request;
        var path = request.Path.Value ?? "/";
        var login = rule.Prefix + LoginPath;
        if (path.Equals(login, StringComparison.OrdinalIgnoreCase))
        {
            await LoginPageAsync(context, rule, domain, login);
            return false;
        }
        if (path.Equals(rule.Prefix + LogoutPath, StringComparison.OrdinalIgnoreCase))
        {
            context.Response.Cookies.Delete(CookieName(rule.RouteId), CookieOptions(context, rule));
            Redirect(context, login);
            return false;
        }
        if (request.Cookies.TryGetValue(CookieName(rule.RouteId), out var cookie) && IsValidSession(cookie, rule))
            return true;

        if ((HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method)) && WantsPage(request))
        {
            Redirect(context, $"{login}?next={Uri.EscapeDataString(path + request.QueryString.Value)}");
            return false;
        }
        var url = $"{request.Scheme}://{request.Host}{login}";
        await DenyAsync(context, ErrorPages.LoginNeeded(domain, url), string.Format(Strings.Access_LoginNeeded, domain, url));
        return false;
    }

    private async Task LoginPageAsync(HttpContext context, AccessRule rule, string domain, string login)
    {
        var request = context.Request;
        var next = SafeNext(request.Query["next"].ToString(), rule.Prefix);
        if (HttpMethods.IsPost(request.Method) && request.HasFormContentType)
        {
            var form = await request.ReadFormAsync(context.RequestAborted);
            next = SafeNext(form["next"].ToString(), rule.Prefix);
            var user = form["user"].ToString();
            if (user == rule.Access.User && AccessSecrets.VerifyPassword(form["password"].ToString(), rule.Access.PasswordHash))
            {
                context.Response.Cookies.Append(CookieName(rule.RouteId), NewSession(rule), CookieOptions(context, rule));
                Redirect(context, next);
                return;
            }
            await Task.Delay(FailureDelay, context.RequestAborted);
            await PageAsync(context, StatusCodes.Status401Unauthorized, ErrorPages.Login(domain, login, next, user, failed: true));
            return;
        }
        await PageAsync(context, StatusCodes.Status200OK, ErrorPages.Login(domain, login, next, null, failed: false));
    }

    private async Task<bool> BasicAsync(HttpContext context, AccessRule rule, string domain)
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            var cacheKey = $"{rule.RouteId:N}|{rule.Access.PasswordHash}|{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(header)))}";
            if (!_checked.TryGetValue(cacheKey, out var ok))
            {
                ok = TryDecodeBasic(header[6..].Trim(), out var user, out var password)
                     && user == rule.Access.User && AccessSecrets.VerifyPassword(password, rule.Access.PasswordHash);
                _checked[cacheKey] = ok;
            }
            if (ok)
                return true;
            await Task.Delay(FailureDelay, context.RequestAborted);
        }
        context.Response.Headers.WWWAuthenticate = $"Basic realm=\"{domain}\", charset=\"UTF-8\"";
        await DenyAsync(context, ErrorPages.BasicNeeded(domain), string.Format(Strings.Access_BasicLead, domain));
        return false;
    }

    private static async Task<bool> HeaderAsync(HttpContext context, AccessRule rule, string domain)
    {
        var name = rule.Access.HeaderName.Trim();
        if (context.Request.Headers.TryGetValue(name, out var values) && values.Any(v => v is not null && AccessSecrets.VerifyKey(v, rule.Access.HeaderHash)))
            return true;
        await DenyAsync(context, ErrorPages.HeaderNeeded(domain, name), string.Format(Strings.Access_HeaderLead, domain, name));
        return false;
    }

    private string NewSession(AccessRule rule)
    {
        var expiry = (time.GetUtcNow() + SessionLifetime).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        return $"{expiry}.{Sign(rule, expiry)}";
    }

    private bool IsValidSession(string cookie, AccessRule rule)
    {
        var dot = cookie.IndexOf('.');
        if (dot <= 0 || !long.TryParse(cookie[..dot], NumberStyles.None, CultureInfo.InvariantCulture, out var expiry)
            || DateTimeOffset.FromUnixTimeSeconds(expiry) <= time.GetUtcNow())
            return false;
        var expected = Encoding.ASCII.GetBytes(Sign(rule, cookie[..dot]));
        return CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(cookie[(dot + 1)..]));
    }

    private string Sign(AccessRule rule, string expiry)
    {
        var payload = Encoding.UTF8.GetBytes($"{rule.RouteId:N}|{expiry}|{rule.Access.User}|{rule.Access.PasswordHash}");
        return Convert.ToBase64String(HMACSHA256.HashData(key, payload)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Until the browser closes; 12 hours at most, by the signed expiry.</summary>
    private static CookieOptions CookieOptions(HttpContext context, AccessRule rule) => new()
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Lax,
        Secure = context.Request.IsHttps,
        Path = rule.Prefix.Length > 0 ? rule.Prefix : "/",
    };

    /// <summary>Back to a path of this route only: no scheme, no other host, so the page is no open redirect.</summary>
    public static string SafeNext(string? next, string prefix)
    {
        var home = prefix.Length > 0 ? prefix : "/";
        if (string.IsNullOrEmpty(next) || !next.StartsWith('/') || next.StartsWith("//", StringComparison.Ordinal) || next.Contains('\\')
            || next.StartsWith(prefix + "/.severino/", StringComparison.OrdinalIgnoreCase))
            return home;
        return next.StartsWith(home, StringComparison.OrdinalIgnoreCase) ? next : home;
    }

    private static bool TryDecodeBasic(string encoded, out string user, out string password)
    {
        user = password = "";
        try
        {
            var text = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            var colon = text.IndexOf(':');
            if (colon < 0)
                return false;
            (user, password) = (text[..colon], text[(colon + 1)..]);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool WantsPage(HttpRequest request) =>
        request.Headers.Accept.Any(a => a?.Contains("text/html", StringComparison.OrdinalIgnoreCase) == true);

    private static void Redirect(HttpContext context, string location)
    {
        RequestLog.MarkFromProxy(context);
        context.Response.StatusCode = StatusCodes.Status303SeeOther;
        context.Response.Headers.Location = location;
        context.Response.Headers.CacheControl = "no-store";
    }

    /// <summary>401: the page for a browser, a line of text for anything else.</summary>
    private static Task DenyAsync(HttpContext context, string page, string text)
    {
        if (WantsPage(context.Request))
            return PageAsync(context, StatusCodes.Status401Unauthorized, page);
        RequestLog.MarkFromProxy(context);
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "text/plain; charset=utf-8";
        return context.Response.WriteAsync(text + "\n");
    }

    private static Task PageAsync(HttpContext context, int status, string page)
    {
        RequestLog.MarkFromProxy(context);
        context.Response.StatusCode = status;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        return context.Response.WriteAsync(page);
    }
}
