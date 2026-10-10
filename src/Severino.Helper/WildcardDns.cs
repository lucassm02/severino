using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Severino.Contracts;

namespace Severino.Helper;

/// <summary>The wildcard names the DNS server answers, swapped whole on each sync.</summary>
public sealed class WildcardTable
{
    private volatile IReadOnlyList<HostEntry> _entries = [];

    /// <summary>Normalized wildcards (<c>*.callfred.sev</c>) and their addresses.</summary>
    public IReadOnlyList<HostEntry> Entries => _entries;

    public void Set(IReadOnlyList<HostEntry> entries) => _entries = entries;

    /// <summary>The addresses of the most specific wildcard above <paramref name="name"/>, or null.</summary>
    public IReadOnlyList<IPAddress>? Lookup(string name)
    {
        var best = _entries
            .Where(e => DomainName.MatchesWildcard(e.Name, name))
            .GroupBy(e => e.Name)
            .OrderByDescending(g => g.Key.Length)
            .FirstOrDefault();
        return best?.Select(e => IPAddress.Parse(e.Address)).ToList();
    }
}

/// <summary>
/// Answers the wildcard names on <see cref="WildcardDnsAddress.Server"/>:53, UDP and TCP. Runs in
/// the Helper, which starts with Windows and holds the port exclusively, so no other program of
/// the user can take it and answer for the NRPT suffixes.
/// </summary>
public sealed class WildcardDnsServer(WildcardTable table, ILogger<WildcardDnsServer> logger, int port = 53) : BackgroundService
{
    private Socket? _udp;
    private TcpListener? _tcp;

    public IPEndPoint EndPoint { get; } = new(IPAddress.Parse(WildcardDnsAddress.Server), port);

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            _udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ExclusiveAddressUse = true };
            _udp.Bind(EndPoint);
            _tcp = new TcpListener(EndPoint) { ExclusiveAddressUse = true };
            _tcp.Start();
        }
        catch (SocketException ex)
        {
            logger.LogError(ex, "Wildcard DNS could not listen on {EndPoint}", EndPoint);
            _udp?.Dispose();
            _udp = null;
        }
        return base.StartAsync(cancellationToken);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        _udp is null || _tcp is null ? Task.CompletedTask : Task.WhenAll(ServeUdpAsync(_udp, stoppingToken), ServeTcpAsync(_tcp, stoppingToken));

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _tcp?.Stop();
        _udp?.Dispose();
        await base.StopAsync(cancellationToken);
    }

    private async Task ServeUdpAsync(Socket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[1500];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var received = await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, cancellationToken);
                if (DnsResponder.Answer(buffer.AsSpan(0, received.ReceivedBytes), table.Lookup) is { } response)
                    await socket.SendToAsync(response, SocketFlags.None, received.RemoteEndPoint, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException ex)
            {
                // A client that went away (ICMP port unreachable) shows up here; keep serving.
                logger.LogDebug(ex, "UDP DNS");
            }
        }
    }

    private async Task ServeTcpAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }
            _ = ServeTcpClientAsync(client, cancellationToken);
        }
    }

    private async Task ServeTcpClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var _ = client;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var stream = client.GetStream();
            var length = new byte[2];
            while (true)
            {
                await stream.ReadExactlyAsync(length, timeout.Token);
                var message = new byte[BinaryPrimitives.ReadUInt16BigEndian(length)];
                await stream.ReadExactlyAsync(message, timeout.Token);
                if (DnsResponder.Answer(message, table.Lookup) is not { } response)
                    return;
                var framed = new byte[response.Length + 2];
                BinaryPrimitives.WriteUInt16BigEndian(framed, (ushort)response.Length);
                response.CopyTo(framed, 2);
                await stream.WriteAsync(framed, timeout.Token);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or EndOfStreamException or SocketException)
        {
        }
    }
}
