using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Yarp.ReverseProxy.Model;

namespace Severino.Proxy;

/// <summary>One request that went through the proxy. Only <see cref="Duration"/> changes, when a WebSocket closes.</summary>
public sealed class RequestEntry
{
    private long _durationTicks = -1;

    public RequestEntry(long sequence, DateTimeOffset time, string scheme, string authority, string method, string pathAndQuery,
        int status, bool fromProxy, bool isWebSocket, TimeSpan? duration, RequestDetails? details = null)
    {
        Details = details;
        Sequence = sequence;
        Time = time;
        Scheme = scheme;
        Authority = authority;
        Host = HostString.FromUriComponent(authority).Host;
        Method = method;
        PathAndQuery = pathAndQuery;
        Status = status;
        FromProxy = fromProxy;
        IsWebSocket = isWebSocket;
        if (duration is { } value)
            _durationTicks = value.Ticks;
    }

    /// <summary>Increasing, unique; the UI asks for everything after the last one it saw.</summary>
    public long Sequence { get; }
    public DateTimeOffset Time { get; }
    public string Scheme { get; }

    /// <summary>Host and port as the browser sent them, e.g. "meuapp.sev:8080".</summary>
    public string Authority { get; }

    public string Host { get; }
    public string Method { get; }
    public string PathAndQuery { get; }
    public int Status { get; }

    /// <summary>Answered by Severino itself: its 404, the 307 to HTTPS, the 502 and 504 pages.</summary>
    public bool FromProxy { get; }

    public bool IsWebSocket { get; }

    /// <summary>Headers, sizes and maybe bodies; null for a service route's TCP connection.</summary>
    public RequestDetails? Details { get; }

    /// <summary>Null while a WebSocket is still open.</summary>
    public TimeSpan? Duration
    {
        get
        {
            var ticks = Interlocked.Read(ref _durationTicks);
            return ticks < 0 ? null : TimeSpan.FromTicks(ticks);
        }
    }

    internal void Close(TimeSpan duration) => Interlocked.Exchange(ref _durationTicks, duration.Ticks);
}

/// <summary>
/// The last <see cref="Capacity"/> requests, in memory only: paths and queries can carry tokens,
/// so nothing goes to disk. Readers poll <see cref="Since"/> instead of getting an event per request.
/// </summary>
public sealed class RequestLog
{
    public const int Capacity = 1000;

    /// <summary>How much of each body is kept while <see cref="CaptureBodies"/> is on.</summary>
    public const int BodyLimit = 64 * 1024;

    /// <summary>Headers kept per request, names and values together.</summary>
    public const int HeaderLimit = 16 * 1024;

    private static readonly object FromProxyKey = new();
    private static readonly object FolderKey = new();
    private static readonly object RouteKey = new();
    private volatile bool _captureBodies;

    private readonly Lock _gate = new();
    private readonly RequestEntry?[] _ring = new RequestEntry?[Capacity];
    private readonly TimeProvider _time;
    private long _next;

    public RequestLog(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    /// <summary>Keeps the first <see cref="BodyLimit"/> bytes of text bodies, both ways. Off by default: it costs memory.</summary>
    public bool CaptureBodies
    {
        get => _captureBodies;
        set => _captureBodies = value;
    }

    /// <summary>Marks the response as written by Severino rather than the route's server.</summary>
    public static void MarkFromProxy(HttpContext context) => context.Items[FromProxyKey] = true;

    /// <summary>A folder route answered: where the files came from, since YARP did not forward it.</summary>
    public static void MarkFolder(HttpContext context, Guid routeId, string folder)
    {
        context.Items[FolderKey] = folder;
        context.Items[RouteKey] = routeId;
    }

    /// <summary>Entries newer than <paramref name="sequence"/> still in the buffer, oldest first.</summary>
    public IReadOnlyList<RequestEntry> Since(long sequence)
    {
        lock (_gate)
        {
            var first = Math.Max(sequence + 1, _next - Capacity);
            var result = new List<RequestEntry>((int)Math.Max(0, _next - first));
            for (var s = first; s < _next; s++)
            {
                if (_ring[s % Capacity] is { } entry && entry.Sequence == s)
                    result.Add(entry);
            }
            return result;
        }
    }

    /// <summary>Forgets everything; sequences keep increasing, so readers just see nothing new.</summary>
    public void Clear()
    {
        lock (_gate)
            Array.Clear(_ring);
    }

    public RequestEntry Add(string scheme, string authority, string method, string pathAndQuery, int status,
        bool fromProxy = false, bool isWebSocket = false, TimeSpan? duration = null, RequestDetails? details = null)
    {
        lock (_gate)
        {
            var entry = new RequestEntry(_next, _time.GetLocalNow(), scheme, authority, method, pathAndQuery, status, fromProxy, isWebSocket, duration, details);
            _ring[_next % Capacity] = entry;
            _next++;
            return entry;
        }
    }

    /// <summary>
    /// Records every request once it is answered. WebSockets are recorded when the upgrade is
    /// answered and closed when the connection ends.
    /// </summary>
    internal async Task RecordAsync(HttpContext context, Func<Task> next)
    {
        var started = Stopwatch.GetTimestamp();
        var request = context.Request;
        var (requestHeaders, requestCut) = Headers(request.Headers);
        RequestEntry? socket = null;
        var isWebSocket = IsWebSocket(context);
        if (isWebSocket)
        {
            context.Response.OnStarting(() =>
            {
                socket = Record(webSocket: true, duration: null, null, null);
                return Task.CompletedTask;
            });
        }

        // Both bodies go through a tap that counts them and, when asked, keeps the first bytes.
        // A WebSocket's bytes are not a body: it is left alone.
        var capture = CaptureBodies ? BodyLimit : 0;
        TapStream? requestTap = null, responseTap = null;
        var originalBody = request.Body;
        var originalResponse = context.Features.Get<IHttpResponseBodyFeature>();
        StreamResponseBodyFeature? tapped = null;
        if (!isWebSocket && originalResponse is not null)
        {
            requestTap = new TapStream(originalBody, CapturedBodies.IsText(request.ContentType) ? capture : 0);
            request.Body = requestTap;
            responseTap = new TapStream(originalResponse.Stream, capture);
            tapped = new StreamResponseBodyFeature(responseTap, originalResponse);
            context.Features.Set<IHttpResponseBodyFeature>(tapped);
        }

        try
        {
            await next();
            if (tapped is not null)
                await tapped.CompleteAsync();
        }
        catch when (!context.Response.HasStarted)
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            throw;
        }
        finally
        {
            if (tapped is not null)
            {
                request.Body = originalBody;
                context.Features.Set(originalResponse);
            }
            var elapsed = Stopwatch.GetElapsedTime(started);
            if (socket is not null)
                socket.Close(elapsed);
            else
                Record(isWebSocket, duration: elapsed, requestTap, responseTap);
        }

        RequestEntry Record(bool webSocket, TimeSpan? duration, TapStream? sent, TapStream? received)
        {
            var (responseHeaders, responseCut) = Headers(context.Response.Headers);
            var proxied = context.Features.Get<IReverseProxyFeature>();
            var routeId = proxied?.Route.Config.RouteId is { } id && Guid.TryParseExact(id, "N", out var guid) ? guid : (Guid?)null;
            var details = new RequestDetails
            {
                RequestHeaders = requestHeaders,
                ResponseHeaders = responseHeaders,
                HeadersCut = requestCut || responseCut,
                Target = proxied?.ProxiedDestination?.Model.Config.Address ?? context.Items[FolderKey] as string,
                RouteId = routeId ?? context.Items[RouteKey] as Guid?,
                RequestBytes = sent?.Count ?? request.ContentLength ?? 0,
                ResponseBytes = received?.Count ?? 0,
                RequestBody = Body(sent),
                ResponseBody = CapturedBodies.IsText(context.Response.ContentType) ? Body(received) : null,
            };
            return Add(request.Scheme, request.Host.Value ?? "", request.Method, request.Path.ToUriComponent() + request.QueryString.ToUriComponent(),
                context.Response.StatusCode, context.Items.ContainsKey(FromProxyKey), webSocket, duration, details);
        }
    }

    private static CapturedBody? Body(TapStream? tap) =>
        tap?.Captured is { Length: > 0 } bytes ? new CapturedBody(bytes, tap.Cut) : null;

    /// <summary>Every name and value, one pair per value, until <see cref="HeaderLimit"/>.</summary>
    private static (IReadOnlyList<KeyValuePair<string, string>> Headers, bool Cut) Headers(IHeaderDictionary headers)
    {
        var result = new List<KeyValuePair<string, string>>();
        var size = 0;
        foreach (var (name, values) in headers)
        {
            foreach (var value in values)
            {
                size += name.Length + (value?.Length ?? 0);
                if (size > HeaderLimit)
                    return (result, true);
                result.Add(new(name, value ?? ""));
            }
        }
        return (result, false);
    }

    // YARP upgrades on its own, without ASP.NET's WebSocket middleware, so context.WebSockets
    // never sees these: read the handshake instead. HTTP/1.1 asks with Upgrade, HTTP/2 with an
    // extended CONNECT.
    private static bool IsWebSocket(HttpContext context)
    {
        var request = context.Request;
        if (context.Features.Get<IHttpExtendedConnectFeature>() is { IsExtendedConnect: true } connect)
            return string.Equals(connect.Protocol, "websocket", StringComparison.OrdinalIgnoreCase);
        return HttpMethods.IsGet(request.Method)
            && request.Headers.Upgrade.Any(value => string.Equals(value, "websocket", StringComparison.OrdinalIgnoreCase));
    }
}
