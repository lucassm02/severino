using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Severino.Core.Network;

namespace Severino.Tests.Network;

public sealed class PortOwnerTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public void Finds_the_process_listening_on_a_port(string address)
    {
        var listener = new TcpListener(IPAddress.Parse(address), 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;

            var owner = PortOwner.Find(port);

            Assert.NotNull(owner);
            Assert.Equal(Environment.ProcessId, owner.ProcessId);
            Assert.Equal(Process.GetCurrentProcess().ProcessName, owner.ProcessName);
            Assert.False(owner.IsHttpSys);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void Free_port_has_no_owner()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        Assert.Null(PortOwner.Find(port));
    }
}
