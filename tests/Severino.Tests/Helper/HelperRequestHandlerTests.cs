using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Severino.Contracts;
using Severino.Helper;

namespace Severino.Tests.Helper;

public sealed class HelperRequestHandlerTests
{
    private readonly FakeHosts _hosts = new();
    private readonly HelperRequestHandler _handler;

    public HelperRequestHandlerTests() =>
        _handler = new HelperRequestHandler(_hosts, NullLogger<HelperRequestHandler>.Instance);

    private HelperResponse Send(HelperRequest request) => _handler.Handle(HelperProtocol.Serialize(request));

    [Fact]
    public void Ping_reports_protocol_version()
    {
        var response = Send(HelperRequest.Ping());

        Assert.True(response.Ok);
        Assert.Equal(HelperProtocol.Version, response.ProtocolVersion);
        Assert.Null(_hosts.Written);
    }

    [Fact]
    public void Sync_writes_normalized_domains()
    {
        var response = Send(HelperRequest.Sync(["B.sev", "a.sev", "b.sev"]));

        Assert.True(response.Ok);
        Assert.Equal(["a.sev", "b.sev"], _hosts.Written!.Select(e => e.Name).Distinct());
    }

    [Fact]
    public void Sync_writes_service_entries_on_their_own_loopback()
    {
        Assert.True(Send(HelperRequest.Sync([new HostEntry("redis.database", "127.77.0.3")])).Ok);

        Assert.Equal([new HostEntry("redis.database", "127.77.0.3")], _hosts.Written);
    }

    [Fact]
    public void Sync_pointing_a_name_off_this_machine_writes_nothing()
    {
        Assert.False(Send(HelperRequest.Sync([new HostEntry("banco.com.br", "203.0.113.10")])).Ok);
        Assert.Null(_hosts.Written);
    }

    [Theory]
    [InlineData("x.sev\r\n127.0.0.1 banco.com.br")]
    [InlineData("localhost")]
    [InlineData("a.sev # comentário")]
    public void Sync_with_bad_domain_writes_nothing(string domain)
    {
        var response = Send(HelperRequest.Sync(["ok.sev", domain]));

        Assert.False(response.Ok);
        Assert.Null(_hosts.Written);
    }

    [Fact]
    public void Sync_over_limit_writes_nothing()
    {
        var entries = Enumerable.Range(0, HelperProtocol.MaxEntries + 1).Select(i => new HostEntry($"d{i}.sev", "127.0.0.1"));

        Assert.False(Send(HelperRequest.Sync(entries)).Ok);
        Assert.Null(_hosts.Written);
    }

    [Fact]
    public void Sync_without_list_is_rejected()
    {
        Assert.False(Send(new HelperRequest(HelperProtocol.SyncCommand)).Ok);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"command\":\"sync\",\"entries\":[{\"name\":\"a.sev\",\"address\":\"127.0.0.1\"},null]}")]
    [InlineData("{\"command\":\"sync\",\"entries\":[{\"name\":\"a.sev\"}]}")]
    [InlineData("{\"command\":\"format-c\"}")]
    public void Malformed_or_unknown_messages_are_rejected(string json)
    {
        Assert.False(_handler.Handle(Encoding.UTF8.GetBytes(json)).Ok);
        Assert.Null(_hosts.Written);
    }

    [Fact]
    public void Write_failure_becomes_error_response()
    {
        _hosts.Fail = true;

        var response = Send(HelperRequest.Sync(["a.sev"]));

        Assert.False(response.Ok);
        Assert.Contains("hosts", response.Error);
    }

    private sealed class FakeHosts : IHostsWriter
    {
        public IReadOnlyList<HostEntry>? Written { get; private set; }
        public bool Fail { get; set; }

        public bool Write(IReadOnlyList<HostEntry> entries)
        {
            if (Fail)
                throw new UnauthorizedAccessException("negado");
            Written = entries;
            return true;
        }
    }
}
