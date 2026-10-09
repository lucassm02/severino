using Severino.Helper;

namespace Severino.Tests.Helper;

public sealed class HostsBlockTests
{
    private const string Original =
        "# Copyright (c) 1993-2009 Microsoft Corp.\r\n" +
        "#\r\n" +
        "127.0.0.1  meu-host-manual\r\n";

    private static string Block(params string[] domains) =>
        HostsBlock.StartMarker + "\r\n" +
        string.Concat(domains.Select(d => $"127.0.0.1  {d}\r\n::1        {d}\r\n")) +
        HostsBlock.EndMarker + "\r\n";

    [Fact]
    public void Appends_block_to_file_without_one()
    {
        var merged = HostsBlock.Merge(Original, ["callfred.sev"]);

        Assert.Equal(Original + Block("callfred.sev"), merged);
    }

    [Fact]
    public void Is_idempotent()
    {
        var once = HostsBlock.Merge(Original, ["b.sev", "a.sev"]);
        var twice = HostsBlock.Merge(once, ["b.sev", "a.sev"]);

        Assert.Equal(once, twice);
    }

    [Fact]
    public void Sorts_domains()
    {
        Assert.EndsWith(Block("a.sev", "b.sev"), HostsBlock.Merge("", ["b.sev", "a.sev"]));
    }

    [Fact]
    public void Replaces_block_in_place_and_keeps_lines_around_it()
    {
        var file = "antes\r\n" + Block("old.sev") + "depois\r\n";

        var merged = HostsBlock.Merge(file, ["new.sev"]);

        Assert.Equal("antes\r\n" + Block("new.sev") + "depois\r\n", merged);
    }

    [Fact]
    public void Empty_list_removes_block()
    {
        var file = "antes\r\n" + Block("old.sev") + "depois\r\n";

        Assert.Equal("antes\r\ndepois\r\n", HostsBlock.Merge(file, []));
    }

    [Fact]
    public void Empty_list_on_file_without_block_changes_nothing()
    {
        Assert.Equal(Original, HostsBlock.Merge(Original, []));
    }

    [Fact]
    public void Adds_newline_before_block_when_file_lacks_one()
    {
        var merged = HostsBlock.Merge("127.0.0.1 x.y", ["a.sev"]);

        Assert.Equal("127.0.0.1 x.y\r\n" + Block("a.sev"), merged);
        Assert.Equal(merged, HostsBlock.Merge(merged, ["a.sev"]));
    }

    [Fact]
    public void Keeps_lf_and_mixed_line_endings_outside_block()
    {
        var file = "linha lf\nlinha crlf\r\n" + Block("old.sev") + "fim lf\n";

        var merged = HostsBlock.Merge(file, ["new.sev"]);

        Assert.Equal("linha lf\nlinha crlf\r\n" + Block("new.sev") + "fim lf\n", merged);
    }

    [Fact]
    public void Recognizes_block_written_with_lf()
    {
        var file = "antes\n" + Block("old.sev").Replace("\r\n", "\n") + "depois\n";

        Assert.Equal("antes\n" + Block("new.sev") + "depois\n", HostsBlock.Merge(file, ["new.sev"]));
    }

    [Fact]
    public void Block_without_end_runs_to_end_of_file()
    {
        var file = "antes\r\n" + HostsBlock.StartMarker + "\r\n127.0.0.1  old.sev\r\nsobra\r\n";

        Assert.Equal("antes\r\n" + Block("new.sev"), HostsBlock.Merge(file, ["new.sev"]));
    }

    [Fact]
    public void Collapses_duplicate_blocks_into_the_first()
    {
        var file = Block("a.sev") + "meio\r\n" + Block("b.sev");

        Assert.Equal(Block("c.sev") + "meio\r\n", HostsBlock.Merge(file, ["c.sev"]));
    }

    [Fact]
    public void Preserves_non_ascii_text_outside_block()
    {
        var file = "# comentário com acentuação\r\n";

        Assert.StartsWith(file, HostsBlock.Merge(file, ["a.sev"]));
    }
}
