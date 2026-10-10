using System.Text;
using System.Text.Json;
using Severino.Contracts;

namespace Severino.Tests.Contracts;

public sealed class HelperProtocolTests
{
    [Fact]
    public void Request_round_trips()
    {
        var request = HelperRequest.Sync(["b.sev", "a.sev"]);

        var parsed = HelperProtocol.DeserializeRequest(HelperProtocol.Serialize(request));

        Assert.Equal(HelperProtocol.SyncCommand, parsed.Command);
        Assert.Equal(["b.sev", "a.sev"], parsed.Domains);
        // A web route's domain goes on both loopbacks.
        Assert.Equal([new HostEntry("b.sev", "127.0.0.1"), new HostEntry("b.sev", "::1")], parsed.Entries!.Take(2));
    }

    [Fact]
    public void Wire_format_has_entries_and_no_domains_field()
    {
        var json = Encoding.UTF8.GetString(HelperProtocol.Serialize(HelperRequest.Sync([new HostEntry("redis", "127.77.0.2")])));

        Assert.Equal("""{"command":"sync","entries":[{"name":"redis","address":"127.77.0.2"}]}""", json);
    }

    [Fact]
    public void Response_round_trips()
    {
        var response = HelperResponse.Failure("falhou", "1.2.3");

        var parsed = HelperProtocol.DeserializeResponse(HelperProtocol.Serialize(response));

        Assert.Equal(response, parsed);
    }

    [Fact]
    public void Serialized_messages_have_no_newline()
    {
        var bytes = HelperProtocol.Serialize(HelperRequest.Sync(["a.sev"]));
        Assert.DoesNotContain((byte)'\n', bytes);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"command\": null}")]
    [InlineData("[]")]
    [InlineData("not json")]
    public void Rejects_malformed_requests(string json)
    {
        Assert.ThrowsAny<JsonException>(() => HelperProtocol.DeserializeRequest(Encoding.UTF8.GetBytes(json)));
    }

    private static IReadOnlyList<HostEntry> Web(params string[] domains) => [.. HostEntry.ForDomains(domains)];

    [Fact]
    public void Normalizes_sorts_and_deduplicates_entries()
    {
        Assert.True(HelperProtocol.TryNormalizeEntries([.. Web("B.sev", "a.sev", "b.sev."), new("Redis", "127.77.0.2")], out var entries, out _));

        Assert.Equal(
        [
            new HostEntry("a.sev", "127.0.0.1"), new HostEntry("a.sev", "::1"),
            new HostEntry("b.sev", "127.0.0.1"), new HostEntry("b.sev", "::1"),
            new HostEntry("redis", "127.77.0.2"),
        ], entries);
    }

    [Fact]
    public void One_invalid_domain_rejects_the_list()
    {
        Assert.False(HelperProtocol.TryNormalizeEntries(Web("a.sev", "x.sev\n127.0.0.1 banco.com.br"), out var entries, out var error));
        Assert.Null(entries);
        Assert.Contains("inválido", error);
    }

    [Theory]
    [InlineData("192.168.203.100")] // the cluster: only the proxy talks to it, never the hosts file
    [InlineData("0.0.0.0")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("fe80::1")]
    [InlineData("127.0.0.1\r\n1.2.3.4 banco.com.br")]
    [InlineData("")]
    public void Only_loopback_addresses_pass(string address)
    {
        Assert.False(HelperProtocol.TryNormalizeEntries([new("redis", address)], out _, out var error));
        Assert.Contains("loopback", error);
    }

    [Fact]
    public void One_label_names_pass_for_service_routes()
    {
        Assert.True(HelperProtocol.TryNormalizeEntries([new("algarbffapi", "127.77.0.9")], out var entries, out _));
        Assert.Equal("algarbffapi", Assert.Single(entries).Name);
        Assert.False(HelperProtocol.TryNormalizeEntries([new("localhost", "127.77.0.9")], out _, out _));
    }

    [Fact]
    public void Rejects_missing_list()
    {
        Assert.False(HelperProtocol.TryNormalizeEntries(null, out _, out _));
    }

    [Fact]
    public void Caps_the_number_of_entries()
    {
        var atLimit = Enumerable.Range(0, HelperProtocol.MaxEntries).Select(i => new HostEntry($"d{i}.sev", "127.0.0.1")).ToList();
        Assert.True(HelperProtocol.TryNormalizeEntries(atLimit, out _, out _));
        Assert.False(HelperProtocol.TryNormalizeEntries([.. atLimit, new("extra.sev", "127.0.0.1")], out _, out _));
    }

    [Fact]
    public async Task Framing_round_trips_a_message()
    {
        using var stream = new MemoryStream();
        await MessageFraming.WriteAsync(stream, "hello"u8.ToArray(), CancellationToken.None);
        stream.Position = 0;

        Assert.Equal("hello"u8.ToArray(), await MessageFraming.ReadAsync(stream, 100, CancellationToken.None));
    }

    [Fact]
    public async Task Framing_rejects_oversized_messages()
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(new string('x', 101) + "\n"));

        await Assert.ThrowsAsync<InvalidDataException>(() => MessageFraming.ReadAsync(stream, 100, CancellationToken.None));
    }

    [Fact]
    public async Task Framing_rejects_truncated_messages()
    {
        using var stream = new MemoryStream("no newline"u8.ToArray());

        await Assert.ThrowsAsync<InvalidDataException>(() => MessageFraming.ReadAsync(stream, 100, CancellationToken.None));
    }
}
