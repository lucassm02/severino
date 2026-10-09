using System.Runtime.InteropServices;
using System.Text;

namespace Severino.Helper;

public interface IHostsWriter
{
    /// <summary>Writes the block for <paramref name="domains"/>; returns false when the file already had it.</summary>
    bool Write(IReadOnlyList<string> domains);
}

/// <summary>Applies <see cref="HostsBlock"/> to a hosts file on disk.</summary>
public sealed partial class HostsFile(string path, Action? flushDns = null) : IHostsWriter
{
    public static string SystemPath { get; } = Path.Combine(Environment.SystemDirectory, "drivers", "etc", "hosts");

    private readonly Action _flushDns = flushDns ?? (() => DnsFlushResolverCache());
    private readonly Lock _gate = new();

    public string BackupPath => Path.Combine(Path.GetDirectoryName(path)!, "hosts.severino.bak");

    public bool Write(IReadOnlyList<string> domains)
    {
        lock (_gate)
        {
            // Latin-1 maps every byte to one char and back, so lines outside the block survive
            // whatever encoding the file uses.
            var current = File.Exists(path) ? Encoding.Latin1.GetString(File.ReadAllBytes(path)) : "";
            var merged = HostsBlock.Merge(current, domains);
            if (merged == current)
                return false;

            var tempPath = path + ".severino.tmp";
            File.WriteAllBytes(tempPath, Encoding.Latin1.GetBytes(merged));
            try
            {
                if (!File.Exists(path))
                {
                    File.Move(tempPath, path);
                }
                else
                {
                    var readOnly = ClearReadOnly(path);
                    ClearReadOnly(BackupPath);
                    try
                    {
                        // Replace keeps the ACL and attributes of the original file.
                        File.Replace(tempPath, path, BackupPath, ignoreMetadataErrors: true);
                    }
                    finally
                    {
                        if (readOnly)
                            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
                    }
                }
            }
            finally
            {
                File.Delete(tempPath);
            }

            _flushDns();
            return true;
        }
    }

    private static bool ClearReadOnly(string file)
    {
        if (!File.Exists(file))
            return false;
        var attributes = File.GetAttributes(file);
        if (!attributes.HasFlag(FileAttributes.ReadOnly))
            return false;
        File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
        return true;
    }

    // Undocumented but stable since Windows 2000; it is what `ipconfig /flushdns` calls.
    [LibraryImport("dnsapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DnsFlushResolverCache();
}
