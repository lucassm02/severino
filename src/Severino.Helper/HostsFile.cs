using System.Runtime.InteropServices;
using System.Security.AccessControl;
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
                var readOnly = false;
                if (File.Exists(path))
                {
                    ClearReadOnly(BackupPath);
                    File.Copy(path, BackupPath, overwrite: true);
                    CopyAccessRules(path, tempPath);
                    readOnly = ClearReadOnly(path);
                }

                // A single rename over the old file: readers never see it missing. File.Replace
                // would move the original away first, leaving a moment with no hosts file at all.
                try
                {
                    MoveWithRetry(tempPath, path);
                }
                finally
                {
                    if (readOnly)
                        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
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

    /// <summary>
    /// Renames over the target, retrying for up to a second: the DNS Client and antivirus open
    /// the hosts file right after it changes, and the rename is refused while they hold it.
    /// </summary>
    private static void MoveWithRetry(string from, string to)
    {
        const int attempts = 20;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(from, to, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < attempts)
            {
                Thread.Sleep(50);
            }
        }
    }

    /// <summary>Gives the new file the same DACL as the one it replaces.</summary>
    private static void CopyAccessRules(string from, string to)
    {
        var sddl = new FileInfo(from).GetAccessControl(AccessControlSections.Access)
            .GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        var security = new FileSecurity();
        security.SetSecurityDescriptorSddlForm(sddl, AccessControlSections.Access);
        new FileInfo(to).SetAccessControl(security);
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
