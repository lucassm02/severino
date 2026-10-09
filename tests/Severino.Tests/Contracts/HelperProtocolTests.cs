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

    [Fact]
    public void Normalizes_sorts_and_deduplicates_domains()
    {
        Assert.True(HelperProtocol.TryNormalizeDomains(["B.sev", "a.sev", "b.sev."], out var domains, out _));
        Assert.Equal(["a.sev", "b.sev"], domains);
    }

    [Fact]
    public void One_invalid_domain_rejects_the_list()
    {
        Assert.False(HelperProtocol.TryNormalizeDomains(["a.sev", "x.sev\n127.0.0.1 banco.com.br"], out var domains, out var error));
        Assert.Null(domains);
        Assert.Contains("inválido", error);
    }

    [Fact]
    public void Rejects_missing_list()
    {
        Assert.False(HelperProtocol.TryNormalizeDomains(null, out _, out _));
    }

    [Fact]
    public void Caps_the_number_of_domains()
    {
        var atLimit = Enumerable.Range(0, HelperProtocol.MaxDomains).Select(i => $"d{i}.sev").ToList();
        Assert.True(HelperProtocol.TryNormalizeDomains(atLimit, out _, out _));
        Assert.False(HelperProtocol.TryNormalizeDomains([.. atLimit, "extra.sev"], out _, out _));
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
