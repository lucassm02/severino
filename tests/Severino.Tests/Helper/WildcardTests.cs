using System.Buffers.Binary;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Severino.Contracts;
using Severino.Helper;

namespace Severino.Tests.Helper;

public sealed class WildcardTests : IDisposable
{
    private readonly string _state = Path.Combine(Path.GetTempPath(), "severino-tests", Guid.NewGuid().ToString("N"), "wildcards.json");
    private readonly WildcardTable _table = new();
    private readonly FakeNrpt _nrpt = new();
    private readonly FakeApprovals _approvals = new();

    public WildcardTests() => Directory.CreateDirectory(Path.GetDirectoryName(_state)!);

    public void Dispose() => Directory.Delete(Path.GetDirectoryName(_state)!, recursive: true);

    private Wildcards NewWildcards() => new(_table, _nrpt, _approvals, NullLogger<Wildcards>.Instance, _state);

    private static byte[] Query(string name, ushort type)
    {
        var message = new List<byte> { 0x12, 0x34, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0 };
        foreach (var label in name.Split('.'))
        {
            message.Add((byte)label.Length);
            message.AddRange(Encoding.ASCII.GetBytes(label));
        }
        message.AddRange([0, (byte)(type >> 8), (byte)type, 0, 1]);
        return [.. message];
    }

    private static (int Rcode, List<IPAddress> Addresses) Read(byte[] response)
    {
        var rcode = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(2)) & 0xF;
        var answers = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(6));
        var offset = 12;
        while (response[offset] != 0)
            offset += response[offset] + 1;
        offset += 5;
        var addresses = new List<IPAddress>();
        for (var i = 0; i < answers; i++)
        {
            var length = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(offset + 10));
            addresses.Add(new IPAddress(response.AsSpan(offset + 12, length)));
            offset += 12 + length;
        }
        return (rcode, addresses);
    }

    [Fact]
    public void The_responder_answers_the_most_specific_wildcard_at_any_depth()
    {
        _table.Set([new HostEntry("*.meuapp.sev", "127.0.0.1"), new HostEntry("*.meuapp.sev", "::1"), new HostEntry("*.dev.meuapp.sev", "10.0.0.5")]);

        Assert.Equal([IPAddress.Loopback], Read(DnsResponder.Answer(Query("a.b.meuapp.sev", 1), _table.Lookup)!).Addresses);
        Assert.Equal([IPAddress.IPv6Loopback], Read(DnsResponder.Answer(Query("cliente42.meuapp.sev", 28), _table.Lookup)!).Addresses);
        Assert.Equal([IPAddress.Parse("10.0.0.5")], Read(DnsResponder.Answer(Query("x.dev.meuapp.sev", 1), _table.Lookup)!).Addresses);
        var mx = Read(DnsResponder.Answer(Query("x.meuapp.sev", 15), _table.Lookup)!); // MX: the name exists, no data
        Assert.Equal(0, mx.Rcode);
        Assert.Empty(mx.Addresses);
        Assert.Equal(3, Read(DnsResponder.Answer(Query("meuapp.sev", 1), _table.Lookup)!).Rcode); // the base is not covered
        Assert.Equal(3, Read(DnsResponder.Answer(Query("outro.sev", 1), _table.Lookup)!).Rcode);
        Assert.Null(DnsResponder.Answer([1, 2, 3], _table.Lookup));
    }

    [Fact]
    public void A_suffix_answers_and_gets_its_rule_only_once_approved()
    {
        var wildcards = NewWildcards();

        var pending = wildcards.SetRoutes([new HostEntry("*.meuapp.sev", "127.0.0.1")]);

        Assert.Single(pending);
        Assert.Null(_table.Lookup("x.meuapp.sev"));
        Assert.Empty(_nrpt.Applied[^1]);

        _approvals.Approve([new HostEntry("*.meuapp.sev", WildcardDnsAddress.Server)]);
        Assert.Empty(wildcards.SetRoutes([new HostEntry("*.meuapp.sev", "127.0.0.1")]));
        Assert.Equal([".meuapp.sev"], _nrpt.Applied[^1]);
        Assert.NotNull(_table.Lookup("x.meuapp.sev"));
    }

    [Fact]
    public void Dns_wildcards_survive_a_restart_and_route_ones_do_not()
    {
        _approvals.Approve([new HostEntry("*.dev.interno", WildcardDnsAddress.Server), new HostEntry("*.meuapp.sev", WildcardDnsAddress.Server)]);
        var first = NewWildcards();
        first.SetDns([new HostEntry("*.dev.interno", "10.0.0.5")]);
        first.SetRoutes([new HostEntry("*.meuapp.sev", "127.0.0.1")]);
        Assert.Equal([".dev.interno", ".meuapp.sev"], _nrpt.Applied[^1]);

        var table = new WildcardTable();
        new Wildcards(table, _nrpt, _approvals, NullLogger<Wildcards>.Instance, _state).Load();

        Assert.Equal([IPAddress.Parse("10.0.0.5")], table.Lookup("x.dev.interno"));
        Assert.Null(table.Lookup("x.meuapp.sev"));
        Assert.Equal([".dev.interno"], _nrpt.Applied[^1]);
    }

    [Fact]
    public void A_public_wildcard_also_needs_its_address_approved()
    {
        _approvals.Approve([new HostEntry("*.staging.exemplo.com", WildcardDnsAddress.Server)]);
        var wildcards = NewWildcards();

        Assert.Single(wildcards.SetDns([new HostEntry("*.staging.exemplo.com", "203.0.113.10")]));

        _approvals.Approve([new HostEntry("*.staging.exemplo.com", "203.0.113.10")]);
        Assert.Empty(wildcards.SetDns([new HostEntry("*.staging.exemplo.com", "203.0.113.10")]));
    }

    [Fact]
    public void Wildcards_never_reach_the_hosts()
    {
        var hosts = new RecordingHosts();
        var handler = new HelperRequestHandler(hosts, _approvals, NullLogger<HelperRequestHandler>.Instance, wildcards: NewWildcards());

        var response = handler.Handle(HelperProtocol.Serialize(HelperRequest.Sync([new HostEntry("*.meuapp.sev", "127.0.0.1"), new HostEntry("meuapp.sev", "127.0.0.1")])));

        Assert.True(response.Ok);
        Assert.Equal([new HostEntry("meuapp.sev", "127.0.0.1")], hosts.Routes);
        Assert.Equal([new HostEntry("*.meuapp.sev", "127.0.0.1")], response.Pending);
        Assert.False(handler.Handle(HelperProtocol.Serialize(HelperRequest.Sync([new HostEntry("*.meuapp.sev", "10.0.0.5")]))).Ok); // routes stay loopback
        Assert.False(handler.Handle(HelperProtocol.Serialize(HelperRequest.Sync([new HostEntry("*.sev", "127.0.0.1")]))).Ok); // never a whole TLD
    }

    private sealed class FakeNrpt : INrptRules
    {
        public List<IReadOnlyCollection<string>> Applied { get; } = [];
        public void Apply(IReadOnlyCollection<string> namespaces) => Applied.Add([.. namespaces]);
    }

    private sealed class FakeApprovals : IDnsApprovals
    {
        private readonly HashSet<HostEntry> _approved = [];
        public bool IsApproved(HostEntry entry) => _approved.Contains(entry);
        public void Approve(IEnumerable<HostEntry> entries) => _approved.UnionWith(entries);
    }

    private sealed class RecordingHosts : IHostsWriter
    {
        public IReadOnlyList<HostEntry>? Routes { get; private set; }
        public bool Write(IReadOnlyList<HostEntry> entries)
        {
            Routes = entries;
            return true;
        }
        public bool WriteDns(IReadOnlyList<HostEntry> entries) => true;
        public bool? Change(Func<string, string?> change) => false;
    }
}
