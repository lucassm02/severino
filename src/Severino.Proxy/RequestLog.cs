using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Severino.Proxy;

/// <summary>One request that went through the proxy. Only <see cref="Duration"/> changes, when a WebSocket closes.</summary>
public sealed class RequestEntry
{
    private long _durationTicks = -1;

    public RequestEntry(long sequence, DateTimeOffset time, string scheme, string authority, string method, string pathAndQuery,
        int status, bool fromProxy, bool isWebSocket, TimeSpan? duration)
    {
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

    private static readonly object FromProxyKey = new();

    private readonly Lock _gate = new();
    private readonly RequestEntry?[] _ring = new RequestEntry?[Capacity];
    private readonly TimeProvider _time;
    private long _next;

    public RequestLog(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    /// <summary>Marks the response as written by Severino rather than the route's server.</summary>
    public static void MarkFromProxy(HttpContext context) => context.Items[FromProxyKey] = true;

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
        bool fromProxy = false, bool isWebSocket = false, TimeSpan? duration = null)
    {
        lock (_gate)
        {
            var entry = new RequestEntry(_next, _time.GetLocalNow(), scheme, authority, method, pathAndQuery, status, fromProxy, isWebSocket, duration);
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
        RequestEntry? socket = null;
        var isWebSocket = IsWebSocket(context);
        if (isWebSocket)
        {
            context.Response.OnStarting(() =>
            {
                socket = Record(webSocket: true, duration: null);
                return Task.CompletedTask;
            });
        }

        try
        {
            await next();
        }
        catch when (!context.Response.HasStarted)
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            throw;
        }
        finally
        {
            var elapsed = Stopwatch.GetElapsedTime(started);
            if (socket is not null)
                socket.Close(elapsed);
            else
                Record(isWebSocket, duration: elapsed);
        }

        RequestEntry Record(bool webSocket, TimeSpan? duration) => Add(
            request.Scheme, request.Host.Value ?? "", request.Method, request.Path.ToUriComponent() + request.QueryString.ToUriComponent(),
            context.Response.StatusCode, context.Items.ContainsKey(FromProxyKey), webSocket, duration);
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
