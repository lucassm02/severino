using System.IO.Pipes;
using System.Security.Principal;
using Microsoft.Extensions.Logging.Abstractions;
using Severino.Contracts;
using Severino.Core.Helper;
using Severino.Helper;

namespace Severino.Tests.Helper;

/// <summary>Real pipe server and client in-process, with the production ACL.</summary>
public sealed class PipeEndToEndTests : IAsyncLifetime
{
    private readonly string _pipeName = $"Severino.Test.{Guid.NewGuid():N}";
    private readonly RecordingHosts _hosts = new();
    private PipeServer _server = null!;

    public async Task InitializeAsync()
    {
        var handler = new HelperRequestHandler(_hosts, NullLogger<HelperRequestHandler>.Instance);
        _server = new PipeServer(handler, NullLogger<PipeServer>.Instance)
        {
            PipeName = _pipeName,
            AllowedUser = WindowsIdentity.GetCurrent().User,
        };
        await _server.StartAsync(CancellationToken.None);

        // ExecuteAsync runs on a background thread since .NET 10: wait until the pipe exists.
        await new HelperClient(_pipeName).SendAsync(HelperRequest.Ping(), CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await _server.StopAsync(CancellationToken.None);
        _server.Dispose();
    }

    [Fact]
    public async Task Allowed_user_can_ping_and_sync_repeatedly()
    {
        var client = new HelperClient(_pipeName);

        Assert.True((await client.SendAsync(HelperRequest.Ping(), CancellationToken.None)).Ok);
        Assert.True((await client.SendAsync(HelperRequest.Sync(["b.sev", "a.sev"]), CancellationToken.None)).Ok);
        Assert.True((await client.SendAsync(HelperRequest.Sync(Array.Empty<HostEntry>()), CancellationToken.None)).Ok);

        Assert.Equal([["a.sev", "b.sev"], []], _hosts.Writes);
    }

    [Fact]
    public async Task Invalid_sync_is_rejected_over_the_wire()
    {
        var response = await new HelperClient(_pipeName).SendAsync(
            HelperRequest.Sync(["ok.sev", "x.sev\r\n127.0.0.1 banco.com.br"]), CancellationToken.None);

        Assert.False(response.Ok);
        Assert.Empty(_hosts.Writes);
    }

    [Fact]
    public async Task Oversized_message_drops_the_connection_and_server_keeps_serving()
    {
        await using (var raw = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await raw.ConnectAsync(2000);
            var junk = new byte[HelperProtocol.MaxMessageBytes + 10];
            Array.Fill(junk, (byte)'x');
            await Assert.ThrowsAnyAsync<IOException>(async () =>
            {
                await raw.WriteAsync(junk);
                await raw.FlushAsync();
                // The server hangs up without answering.
                var buffer = new byte[1];
                if (await raw.ReadAsync(buffer) == 0)
                    throw new IOException("closed");
            });
        }

        Assert.True((await new HelperClient(_pipeName).SendAsync(HelperRequest.Ping(), CancellationToken.None)).Ok);
    }

    [Fact]
    public void No_other_process_can_open_a_second_server_instance()
    {
        // Denied (access or pipe busy) either way.
        Assert.ThrowsAny<SystemException>(() =>
            new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1).Dispose());
    }

    [Fact]
    public async Task Missing_server_is_reported_as_unavailable()
    {
        var client = new HelperClient($"Severino.Test.Missing.{Guid.NewGuid():N}");

        await Assert.ThrowsAsync<HelperUnavailableException>(() => client.SendAsync(HelperRequest.Ping(), CancellationToken.None));
    }

    private sealed class RecordingHosts : IHostsWriter
    {
        private readonly Lock _gate = new();
        private readonly List<string[]> _writes = [];

        public List<string[]> Writes
        {
            get { lock (_gate) return [.. _writes]; }
        }

        public bool Write(IReadOnlyList<HostEntry> entries)
        {
            lock (_gate) _writes.Add([.. entries.Select(e => e.Name).Distinct()]);
            return true;
        }
    }
}
