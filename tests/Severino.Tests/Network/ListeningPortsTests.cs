using System.Net;
using System.Net.Sockets;
using Severino.Core.Network;

namespace Severino.Tests.Network;

public sealed class ListeningPortsTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public void Lists_a_socket_this_test_opened(string address)
    {
        // Port 0 would land in the dynamic range, which the list leaves out for anything but dev runtimes.
        var listener = StartBelowDynamicRange(IPAddress.Parse(address));
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;

            var mine = ListeningPorts.List().Where(p => p.Port == port).ToList();
            var excluded = ListeningPorts.List(excludeProcessId: Environment.ProcessId).Where(p => p.Port == port);

            Assert.Equal(Environment.ProcessId, Assert.Single(mine).ProcessId);
            Assert.Empty(excluded);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static TcpListener StartBelowDynamicRange(IPAddress address)
    {
        for (var port = Random.Shared.Next(20000, 40000); ; port++)
        {
            var listener = new TcpListener(address, port);
            try
            {
                listener.Start();
                return listener;
            }
            catch (SocketException)
            {
                // taken: try the next one
            }
        }
    }

    [Fact]
    public void Reads_the_command_line_of_a_process()
    {
        Assert.Contains(Path.GetFileName(Environment.ProcessPath!), ListeningPorts.CommandLine(Environment.ProcessId));
    }

    [Fact]
    public void Keeps_what_could_be_a_dev_server()
    {
        TcpListenerRow Row(string address, int port, int pid) => new(IPAddress.Parse(address), port, pid);
        var names = new Dictionary<int, (string, string?)>
        {
            [10] = ("node", "vite"),
            [11] = ("svchost", null),
            [12] = ("Spotify", null),
            [13] = ("dotnet", null),
            [14] = ("nginx", null),
        };

        var ports = ListeningPorts.Filter(
        [
            Row("0.0.0.0", 5173, 10),
            Row("::", 5173, 10),          // same port on IPv6: listed once
            Row("192.168.0.10", 3000, 10), // a LAN address only: 127.0.0.1 cannot reach it
            Row("127.0.0.1", 135, 11),    // below 1024
            Row("127.0.0.1", 8080, 11),   // a Windows service
            Row("127.0.0.1", 57621, 12),  // dynamic range, not a dev runtime
            Row("127.0.0.1", 50123, 13),  // dynamic range, but dotnet
            Row("127.0.0.1", 80, 14),     // 80 stays
            Row("127.0.0.1", 9000, 4),    // http.sys
            Row("127.0.0.1", 7000, 99),   // the app itself
        ], excludeProcessId: 99, pid => names[pid]);

        Assert.Equal(["80 · nginx", "5173 · node (vite)", "50123 · dotnet"], ports.Select(p => p.Label));
    }

    [Theory]
    [InlineData(@"""C:\Program Files\nodejs\node.exe"" C:\src\app\node_modules\vite\bin\vite.js --port 5173", "vite")]
    [InlineData(@"node C:/src/app/node_modules/.bin/../next/dist/bin/next dev", "next")]
    [InlineData(@"node C:\src\app\node_modules\nuxi\bin\nuxi.mjs dev", "nuxt")]
    [InlineData(@"node C:\src\site\node_modules\astro\astro.js dev", "astro")]
    [InlineData(@"node C:\src\ui\node_modules\storybook\bin\index.cjs dev -p 6006", "storybook")]
    [InlineData(@"node C:\src\web\node_modules\@angular\cli\bin\ng.js serve", "angular")]
    [InlineData(@"node C:\src\cra\node_modules\react-scripts\scripts\start.js", "webpack")]
    [InlineData(@"""C:\Program Files\dotnet\dotnet.exe"" exec ""C:\Program Files\dotnet\sdk\10.0.401\DotnetTools\dotnet-watch\10.0.401-servicing\tools\net10.0\any\dotnet-watch.dll"" run", "dotnet watch")]
    [InlineData(@"node server.js", null)]
    [InlineData(@"node C:\src\next-steps\server.js", null)]
    [InlineData(null, null)]
    public void Recognises_the_tool_from_the_command_line(string? commandLine, string? tool)
    {
        Assert.Equal(tool, ListeningPorts.ToolFrom(commandLine));
    }
}
