using System.Net.Sockets;
using Yarp.ReverseProxy.Forwarder;

namespace Severino.Proxy;

/// <summary>YARP's client factory, with connections opened by <see cref="ParallelConnect"/>.</summary>
internal sealed class ProxyHttpClientFactory : ForwarderHttpClientFactory
{
    protected override void ConfigureHandler(ForwarderHttpClientContext context, SocketsHttpHandler handler)
    {
        base.ConfigureHandler(context, handler);
        handler.ConnectCallback = async (connection, cancellationToken) =>
        {
            var socket = await ParallelConnect.ConnectAsync(connection.DnsEndPoint.Host, connection.DnsEndPoint.Port, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        };
    }
}
