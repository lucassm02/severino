using Microsoft.Win32;
using Severino.Contracts;

namespace Severino.Helper;

public interface IDnsApprovals
{
    /// <summary>Whether an administrator approved this name on this public address.</summary>
    bool IsApproved(HostEntry entry);

    /// <summary>Records the approvals; only an elevated process can write them.</summary>
    void Approve(IEnumerable<HostEntry> entries);
}

/// <summary>
/// The name and public address pairs an administrator approved, in HKLM, which only
/// administrators write: what lets the Helper put a public address in the hosts without
/// trusting the pipe. Written by <c>Severino.Helper.exe --approve-dns</c>, run elevated.
/// </summary>
public sealed class DnsApprovals(string keyPath = DnsApprovals.DefaultKey) : IDnsApprovals
{
    public const string DefaultKey = PipeAccess.RegistryKey + @"\ApprovedDns";

    public bool IsApproved(HostEntry entry)
    {
        using var key = Registry.LocalMachine.OpenSubKey(keyPath);
        return key?.GetValue(ValueName(entry)) is not null;
    }

    public void Approve(IEnumerable<HostEntry> entries)
    {
        using var key = Registry.LocalMachine.CreateSubKey(keyPath);
        foreach (var entry in entries)
            key.SetValue(ValueName(entry), DateTime.Now.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Forgets every approval, on uninstall.</summary>
    public void Clear() => Registry.LocalMachine.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);

    private static string ValueName(HostEntry entry) => $"{entry.Name} {entry.Address}";
}
