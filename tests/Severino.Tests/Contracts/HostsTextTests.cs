using Severino.Contracts;
using Severino.Helper;

namespace Severino.Tests.Contracts;

public sealed class HostsTextTests
{
    private static readonly DateTime When = new(2026, 10, 10, 14, 32, 0);

    [Theory]
    [InlineData("127.0.0.1", AddressScope.Loopback)]
    [InlineData("::1", AddressScope.Loopback)]
    [InlineData("10.20.30.40", AddressScope.Private)]
    [InlineData("172.31.255.1", AddressScope.Private)]
    [InlineData("192.168.203.100", AddressScope.Private)]
    [InlineData("100.100.1.1", AddressScope.Private)]
    [InlineData("169.254.10.10", AddressScope.Private)]
    [InlineData("fd12:3456::1", AddressScope.Private)]
    [InlineData("172.32.0.1", AddressScope.Public)]
    [InlineData("8.8.8.8", AddressScope.Public)]
    [InlineData("2001:db8::1", AddressScope.Public)]
    public void Addresses_are_classified(string address, AddressScope scope)
    {
        Assert.True(DnsAddress.TryClassify(address, out _, out var actual));
        Assert.Equal(scope, actual);
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("239.1.1.1")]
    [InlineData("ff02::1")]
    [InlineData("10.0.0.1 ")]
    [InlineData("sql.interno")]
    public void Non_host_addresses_are_refused(string address)
    {
        Assert.False(DnsAddress.TryClassify(address, out _, out _));
    }

    private const string Sample =
        "# Copyright (c) 1993-2009 Microsoft Corp.\r\n" +
        "#\r\n" +
        "#      102.54.94.97     rhino.acme.com          # source server\r\n" +
        "\r\n" +
        "10.0.0.8 sql.interno sql # banco\r\n" +
        "\r\n" +
        "# Added by Docker Desktop\r\n" +
        "192.168.1.5 host.docker.internal\r\n" +
        "# End of section\r\n" +
        "# >>> Severino managed block (do not edit)\r\n" +
        "127.0.0.1  meuapp.sev\r\n" +
        "# <<< Severino\r\n" +
        "# >>> Severino DNS (do not edit)\r\n" +
        "10.0.0.20  api.interno\r\n" +
        "# <<< Severino DNS\r\n";

    [Fact]
    public void Lines_outside_the_blocks_are_read_with_their_origin()
    {
        var lines = HostsText.ExternalLines(Sample);

        Assert.Equal(2, lines.Count); // the commented example and the blocks are not entries
        Assert.Equal(new[] { "sql.interno", "sql" }, lines[0].Names);
        Assert.Equal("10.0.0.8 sql.interno sql # banco", lines[0].Text);
        Assert.Null(lines[0].Origin);
        Assert.Equal("host.docker.internal", Assert.Single(lines[1].Names));
        Assert.Equal("# Added by Docker Desktop", lines[1].Origin);
    }

    [Fact]
    public void An_edit_keeps_the_first_version_in_the_note()
    {
        var once = HostsText.ReplaceLine(Sample, "10.0.0.8 sql.interno sql # banco", "10.0.0.9   sql.interno", When)!;
        var twice = HostsText.ReplaceLine(once, "10.0.0.9   sql.interno", "10.0.0.10  sql.interno", When.AddHours(1))!;

        Assert.Contains(
            "# Severino: esta linha nao foi criada pelo Severino; editada em 2026-10-10 15:32. Antes: 10.0.0.8 sql.interno sql # banco\r\n" +
            "10.0.0.10  sql.interno\r\n", twice);
        Assert.Single(twice.Split('\n'), l => l.StartsWith(HostsText.NotePrefix));

        var line = HostsText.ExternalLines(twice)[0];
        Assert.StartsWith(HostsText.NotePrefix, line.Note);
        Assert.False(line.Removed);
    }

    [Fact]
    public void A_removed_line_is_commented_and_still_listed()
    {
        var removed = HostsText.ReplaceLine(Sample, "10.0.0.8 sql.interno sql # banco", null, When)!;

        Assert.Contains("# 10.0.0.8 sql.interno sql # banco\r\n", removed);
        var line = HostsText.ExternalLines(removed)[0];
        Assert.True(line.Removed);
        Assert.Equal("10.0.0.8", line.Address);
    }

    [Fact]
    public void Lines_inside_the_blocks_and_missing_lines_cannot_be_edited()
    {
        Assert.Null(HostsText.ReplaceLine(Sample, "127.0.0.1  meuapp.sev", "127.0.0.1  outro.sev", When));
        Assert.Null(HostsText.ReplaceLine(Sample, "10.0.0.20  api.interno", "10.0.0.21  api.interno", When));
        Assert.Null(HostsText.ReplaceLine(Sample, "10.0.0.99 nao.existe", "10.0.0.1 x", When));
    }

    [Fact]
    public void The_two_blocks_live_side_by_side()
    {
        var withDns = HostsBlock.MergeDns(Sample, [new HostEntry("api.interno", "10.0.0.21")]);
        var routesEmptied = HostsBlock.Merge(withDns, Array.Empty<HostEntry>());

        Assert.Contains("10.0.0.21  api.interno", routesEmptied);
        Assert.DoesNotContain("meuapp.sev", routesEmptied);
        Assert.Contains("# >>> Severino DNS (do not edit)", routesEmptied);
        Assert.Contains("10.0.0.8 sql.interno sql # banco", routesEmptied);
        Assert.Equal(routesEmptied, HostsBlock.Merge(routesEmptied, Array.Empty<HostEntry>()));
        Assert.DoesNotContain("api.interno", HostsBlock.MergeDns(routesEmptied, Array.Empty<HostEntry>()));
    }
}
