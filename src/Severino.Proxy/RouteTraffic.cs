using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Severino.Core.Configuration;
using Yarp.ReverseProxy.Model;

namespace Severino.Proxy;

/// <summary>
/// A route's <see cref="TrafficRules"/> other than headers, which are YARP transforms: CORS, the
/// delay and the simulated failure, in that order, in the route's pipeline before forwarding.
/// </summary>
internal static class RouteTraffic
{
    /// <param name="rules">By YARP route id; routes without rules are missing.</param>
    public static async Task ApplyAsync(HttpContext context, Func<Task> next, IReadOnlyDictionary<string, TrafficRules> rules)
    {
        var route = context.GetReverseProxyFeature().Route.Config;
        if (!rules.TryGetValue(route.RouteId, out var rule))
        {
            await next();
            return;
        }

        if (rule.Cors)
        {
            if (AnswerPreflight(context))
                return;
            AllowOrigin(context);
        }

        if (rule.DelayMs > 0)
        {
            try
            {
                await Task.Delay(rule.DelayMs, context.RequestAborted);
            }
            catch (OperationCanceledException)
            {
                return; // The browser gave up waiting.
            }
        }

        if (rule.FailPercent > 0 && Random.Shared.Next(100) < rule.FailPercent)
        {
            var domain = route.Metadata?.GetValueOrDefault(ProxyConfigMapper.DomainKey) ?? context.Request.Host.Host;
            RequestLog.MarkFromProxy(context);
            context.Response.StatusCode = rule.FailStatus;
            if (rule.FailStatus == StatusCodes.Status429TooManyRequests)
                context.Response.Headers.RetryAfter = "1";
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.WriteAsync(ErrorPages.Simulated(domain, rule.FailStatus, rule.FailPercent));
            return;
        }

        await next();
    }

    /// <summary>An OPTIONS with Origin and Access-Control-Request-Method: Severino says yes, without bothering the server.</summary>
    private static bool AnswerPreflight(HttpContext context)
    {
        var request = context.Request;
        if (!HttpMethods.IsOptions(request.Method) || StringValues.IsNullOrEmpty(request.Headers.Origin)
            || StringValues.IsNullOrEmpty(request.Headers.AccessControlRequestMethod))
            return false;

        var headers = context.Response.Headers;
        RequestLog.MarkFromProxy(context);
        context.Response.StatusCode = StatusCodes.Status204NoContent;
        headers.AccessControlAllowOrigin = request.Headers.Origin;
        headers.AccessControlAllowCredentials = "true";
        headers.AccessControlAllowMethods = request.Headers.AccessControlRequestMethod;
        if (!StringValues.IsNullOrEmpty(request.Headers.AccessControlRequestHeaders))
            headers.AccessControlAllowHeaders = request.Headers.AccessControlRequestHeaders;
        // Chrome asks before a public page calls a local address.
        if (request.Headers["Access-Control-Request-Private-Network"] == "true")
            headers["Access-Control-Allow-Private-Network"] = "true";
        headers.AccessControlMaxAge = "600";
        headers.Vary = "Origin";
        return true;
    }

    /// <summary>
    /// Lets the requesting origin read the answer, whatever the server said about CORS: set when
    /// the answer starts, so error pages and simulated failures carry it too.
    /// </summary>
    private static void AllowOrigin(HttpContext context)
    {
        var origin = context.Request.Headers.Origin;
        if (StringValues.IsNullOrEmpty(origin))
            return;
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers.AccessControlAllowOrigin = origin;
            headers.AccessControlAllowCredentials = "true";
            // With credentials "*" means a header named "*": list the real ones.
            headers.AccessControlExposeHeaders = string.Join(", ", headers.Keys.Where(k => !k.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase)));
            headers.Vary = StringValues.IsNullOrEmpty(headers.Vary) ? "Origin" : StringValues.Concat(headers.Vary, "Origin");
            return Task.CompletedTask;
        });
    }
}
