using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Win32;

namespace Severino.Helper;

/// <summary>Who may talk to the Helper, and the pipe ACL that enforces it.</summary>
public static class PipeAccess
{
    public const string RegistryKey = @"SOFTWARE\Severino\Helper";
    public const string AllowedSidValue = "AllowedUserSid";

    /// <summary>
    /// SYSTEM gets full control, <paramref name="user"/> may connect, read and write, and any
    /// network logon is denied. The server keeps a single pipe instance, so the right to create
    /// instances (which a read/write client open also requests) gives the user nothing.
    /// </summary>
    public static PipeSecurity CreateSecurity(SecurityIdentifier user)
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            user,
            PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance | PipeAccessRights.Synchronize,
            AccessControlType.Allow));
        return security;
    }

    /// <summary>
    /// The SID written by the installer (or scripts/dev-helper.ps1) in HKLM, which only admins can
    /// change. Run from a console for debugging, it falls back to the current user.
    /// </summary>
    public static SecurityIdentifier? ReadAllowedUser()
    {
        using var key = Registry.LocalMachine.OpenSubKey(RegistryKey);
        if (key?.GetValue(AllowedSidValue) is string value)
        {
            try
            {
                return new SecurityIdentifier(value);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        return WindowsServiceHelpers.IsWindowsService() ? null : WindowsIdentity.GetCurrent().User;
    }
}
