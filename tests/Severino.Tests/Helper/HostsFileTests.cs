using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Severino.Helper;

namespace Severino.Tests.Helper;

public sealed class HostsFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "severino-tests", Guid.NewGuid().ToString("N"));
    private readonly string _path;
    private int _flushes;
    private readonly HostsFile _hosts;

    public HostsFileTests()
    {
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "hosts");
        _hosts = new HostsFile(_path, () => _flushes++);
    }

    public void Dispose()
    {
        foreach (var file in Directory.GetFiles(_dir))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Writes_block_keeps_backup_and_flushes_dns()
    {
        var original = Encoding.Latin1.GetBytes("# comentário em Latin-1\r\n");
        File.WriteAllBytes(_path, original);

        Assert.True(_hosts.Write(["meuapp.sev"]));

        var written = File.ReadAllBytes(_path);
        Assert.Equal(original, written[..original.Length]);
        Assert.Contains("127.0.0.1  meuapp.sev", Encoding.ASCII.GetString(written));
        Assert.Equal(original, File.ReadAllBytes(_hosts.BackupPath));
        Assert.Equal(1, _flushes);
    }

    [Fact]
    public void Writes_without_bom()
    {
        File.WriteAllText(_path, "");

        _hosts.Write(["a.sev"]);

        Assert.Equal((byte)'#', File.ReadAllBytes(_path)[0]);
    }

    [Fact]
    public void Unchanged_content_is_not_rewritten()
    {
        File.WriteAllText(_path, "");
        _hosts.Write(["a.sev"]);
        var stamp = File.GetLastWriteTimeUtc(_path);

        Assert.False(_hosts.Write(["a.sev"]));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(_path));
        Assert.Equal(1, _flushes);
    }

    [Fact]
    public void Keeps_read_only_attribute_and_can_write_again()
    {
        File.WriteAllText(_path, "");
        File.SetAttributes(_path, FileAttributes.ReadOnly);

        _hosts.Write(["a.sev"]);
        _hosts.Write(["b.sev"]);

        Assert.True(File.GetAttributes(_path).HasFlag(FileAttributes.ReadOnly));
        Assert.Contains("b.sev", File.ReadAllText(_path));
    }

    [Fact]
    public async Task File_never_goes_missing_while_rewritten()
    {
        File.WriteAllText(_path, "127.0.0.1  manual.host\r\n");
        var missing = 0;
        using var stop = new CancellationTokenSource();

        var reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    File.ReadAllBytes(_path);
                }
                catch (FileNotFoundException)
                {
                    Interlocked.Increment(ref missing);
                }
                catch (IOException)
                {
                    // Sharing violation while the rename lands: the file is there.
                }
            }
        });

        for (var i = 0; i < 100; i++)
            _hosts.Write(i % 2 == 0 ? ["a.sev"] : ["a.sev", "b.sev"]);
        await stop.CancelAsync();
        await reader;

        Assert.Equal(0, missing);
    }

    [Fact]
    public void Keeps_the_original_access_rules()
    {
        File.WriteAllText(_path, "");
        var guests = new SecurityIdentifier(WellKnownSidType.BuiltinGuestsSid, null);
        var security = new FileInfo(_path).GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(guests, FileSystemRights.ReadData, AccessControlType.Allow));
        new FileInfo(_path).SetAccessControl(security);

        _hosts.Write(["a.sev"]);

        var rules = new FileInfo(_path).GetAccessControl()
            .GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>();
        Assert.Contains(rules, r => r.IdentityReference.Equals(guests) && r.FileSystemRights.HasFlag(FileSystemRights.ReadData));
    }

    [Fact]
    public void Leaves_no_temp_file()
    {
        File.WriteAllText(_path, "");

        _hosts.Write(["a.sev"]);

        Assert.False(File.Exists(_path + ".severino.tmp"));
    }
}
