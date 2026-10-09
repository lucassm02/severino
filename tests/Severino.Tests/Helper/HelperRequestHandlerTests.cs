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
        Assert.Equal(["a.sev", "b.sev"], _hosts.Written);
    }

    [Theory]
    [InlineData("x.sev\r\n127.0.0.1 banco.com.br")]
    [InlineData("callfred")]
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
        var domains = Enumerable.Range(0, HelperProtocol.MaxDomains + 1).Select(i => $"d{i}.sev");

        Assert.False(Send(HelperRequest.Sync(domains)).Ok);
        Assert.Null(_hosts.Written);
    }

    [Fact]
    public void Sync_without_list_is_rejected()
    {
        Assert.False(Send(new HelperRequest(HelperProtocol.SyncCommand)).Ok);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"command\":\"sync\",\"domains\":[\"a.sev\",null]}")]
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
        public IReadOnlyList<string>? Written { get; private set; }
        public bool Fail { get; set; }

        public bool Write(IReadOnlyList<string> domains)
        {
            if (Fail)
                throw new UnauthorizedAccessException("negado");
            Written = domains;
            return true;
        }
    }
}
