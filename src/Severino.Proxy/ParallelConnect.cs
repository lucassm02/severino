using System.Net;
using System.Net.Sockets;

namespace Severino.Proxy;

/// <summary>
/// Connects to every address of a host at once and keeps the first that answers.
/// On Windows a refused connection takes about 2 s to fail, so trying "localhost" as ::1 and then
/// 127.0.0.1 in turn stalls every new connection to a dev server that listens on only one of them.
/// </summary>
public static class ParallelConnect
{
    public static async Task<Socket> ConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        IPAddress[] addresses = IPAddress.TryParse(host.Trim('[', ']'), out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(host, cancellationToken);
        if (addresses.Length == 0)
            throw new SocketException((int)SocketError.HostNotFound);

        using var losers = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var attempts = addresses.Select(a => ConnectOneAsync(a, port, losers.Token)).ToList();
        Exception? lastError = null;

        while (attempts.Count > 0)
        {
            var finished = await Task.WhenAny(attempts);
            attempts.Remove(finished);
            try
            {
                var socket = await finished;
                await losers.CancelAsync();
                _ = DisposeAllAsync(attempts);
                return socket;
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                lastError = ex;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        throw lastError!;
    }

    private static async Task<Socket> ConnectOneAsync(IPAddress address, int port, CancellationToken cancellationToken)
    {
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(address, port, cancellationToken);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>Closes sockets from attempts that connected after the winner.</summary>
    private static async Task DisposeAllAsync(IEnumerable<Task<Socket>> attempts)
    {
        foreach (var attempt in attempts)
        {
            try
            {
                (await attempt).Dispose();
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
            }
        }
    }
}
