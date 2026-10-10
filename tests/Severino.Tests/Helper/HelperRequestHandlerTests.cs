using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Severino.Contracts;
using Severino.Helper;

namespace Severino.Tests.Helper;

public sealed class HelperRequestHandlerTests
{
    private readonly FakeHosts _hosts = new();
    private readonly FakeApprovals _approvals = new();
    private readonly HelperRequestHandler _handler;

    public HelperRequestHandlerTests() =>
        _handler = new HelperRequestHandler(_hosts, _approvals, NullLogger<HelperRequestHandler>.Instance, () => new DateTime(2026, 10, 10, 14, 32, 0));

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

    [Fact]
    public void Dns_sync_writes_private_addresses_and_holds_public_ones_until_approved()
    {
        var response = Send(HelperRequest.SyncDns(
        [
            new HostEntry("sql.interno", "10.0.0.8"),
            new HostEntry("vpn.casa", "100.64.1.2"),
            new HostEntry("site.novo", "203.0.113.10"),
        ]));

        Assert.True(response.Ok);
        Assert.Equal(["sql.interno", "vpn.casa"], _hosts.WrittenDns!.Select(e => e.Name));
        Assert.Equal([new HostEntry("site.novo", "203.0.113.10")], response.Pending);

        _approvals.Approved.Add(new HostEntry("site.novo", "203.0.113.10"));
        response = Send(HelperRequest.SyncDns([new HostEntry("site.novo", "203.0.113.10")]));
        Assert.Null(response.Pending);
        Assert.Equal([new HostEntry("site.novo", "203.0.113.10")], _hosts.WrittenDns);
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("fe80::1%3")]
    [InlineData("10.0.0.8:80")]
    public void Dns_sync_refuses_what_is_not_a_host_address(string address)
    {
        Assert.False(Send(HelperRequest.SyncDns([new HostEntry("x.interno", address)])).Ok);
        Assert.Null(_hosts.WrittenDns);
    }

    [Fact]
    public void The_routes_block_still_takes_loopback_only()
    {
        Assert.False(Send(HelperRequest.Sync([new HostEntry("sql.interno", "10.0.0.8")])).Ok);
    }

    [Fact]
    public void A_line_from_outside_is_replaced_under_a_note()
    {
        _hosts.Text = "# feito à mão\r\n10.0.0.8 sql.interno\r\n";

        var response = Send(HelperRequest.EditLine("10.0.0.8 sql.interno", [new HostEntry("sql.interno", "10.0.0.9"), new HostEntry("sql", "10.0.0.9")]));

        Assert.True(response.Ok, response.Error);
        Assert.Equal(
            "# feito à mão\r\n" +
            "# Severino: esta linha nao foi criada pelo Severino; editada em 2026-10-10 14:32. Antes: 10.0.0.8 sql.interno\r\n" +
            "10.0.0.9   sql.interno sql\r\n", _hosts.Text);
    }

    [Fact]
    public void A_line_that_changed_meanwhile_is_left_alone()
    {
        _hosts.Text = "10.0.0.7 sql.interno\r\n";

        var response = Send(HelperRequest.EditLine("10.0.0.8 sql.interno", [new HostEntry("sql.interno", "10.0.0.9")]));

        Assert.False(response.Ok);
        Assert.StartsWith("A linha mudou", response.Error);
        Assert.Equal("10.0.0.7 sql.interno\r\n", _hosts.Text);
    }

    [Fact]
    public void Editing_a_line_to_a_public_address_asks_for_approval()
    {
        _hosts.Text = "10.0.0.8 sql.interno\r\n";

        var response = Send(HelperRequest.EditLine("10.0.0.8 sql.interno", [new HostEntry("sql.interno", "203.0.113.10")]));

        Assert.False(response.Ok);
        Assert.Equal([new HostEntry("sql.interno", "203.0.113.10")], response.Pending);
        Assert.Equal("10.0.0.8 sql.interno\r\n", _hosts.Text);
    }

    [Fact]
    public void Removing_a_line_from_outside_comments_it()
    {
        _hosts.Text = "10.0.0.8 sql.interno\r\n";

        Assert.True(Send(HelperRequest.EditLine("10.0.0.8 sql.interno", [])).Ok);

        Assert.Equal(
            "# Severino: esta linha nao foi criada pelo Severino; removida em 2026-10-10 14:32. Antes: 10.0.0.8 sql.interno\r\n" +
            "# 10.0.0.8 sql.interno\r\n", _hosts.Text);
    }

    private sealed class FakeHosts : IHostsWriter
    {
        public IReadOnlyList<HostEntry>? Written { get; private set; }
        public IReadOnlyList<HostEntry>? WrittenDns { get; private set; }
        public string Text { get; set; } = "";
        public bool Fail { get; set; }

        public bool Write(IReadOnlyList<HostEntry> entries)
        {
            if (Fail)
                throw new UnauthorizedAccessException("negado");
            Written = entries;
            return true;
        }

        public bool WriteDns(IReadOnlyList<HostEntry> entries)
        {
            WrittenDns = entries;
            return true;
        }

        public bool? Change(Func<string, string?> change)
        {
            var changed = change(Text);
            if (changed is null)
                return null;
            var written = changed != Text;
            Text = changed;
            return written;
        }
    }

    private sealed class FakeApprovals : IDnsApprovals
    {
        public List<HostEntry> Approved { get; } = [];
        public bool IsApproved(HostEntry entry) => Approved.Contains(entry);
        public void Approve(IEnumerable<HostEntry> entries) => Approved.AddRange(entries);
    }
}
