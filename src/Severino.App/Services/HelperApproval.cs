using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using Severino.Contracts;

namespace Severino.App.Services;

/// <summary>
/// The two things in the DNS tab that need an administrator: approving public addresses (the
/// Helper's own binary, run elevated) and opening the hosts file to edit by hand. Both go
/// through Windows' UAC prompt, which is the consent.
/// </summary>
public static class HelperApproval
{
    private const string ServiceKey = @"SYSTEM\CurrentControlSet\Services\Severino.Helper";
    private const int Cancelled = 1223; // ERROR_CANCELLED: the person said no to UAC

    /// <summary>
    /// Runs <c>Severino.Helper.exe --approve-dns</c> elevated for <paramref name="entries"/>. Null
    /// when it worked, else why not.
    /// </summary>
    public static async Task<string?> ApproveAsync(IReadOnlyList<HostEntry> entries)
    {
        if (HelperPath() is not { } helper)
            return "O serviço auxiliar não está instalado.";
        try
        {
            using var process = Process.Start(new ProcessStartInfo(helper, $"--approve-dns {HelperProtocol.EncodeApproval(entries)}")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (process is null)
                return "O Windows não abriu o serviço auxiliar.";
            await process.WaitForExitAsync();
            return process.ExitCode == 0 ? null : $"A aprovação falhou (código {process.ExitCode}).";
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == Cancelled)
        {
            return "A aprovação foi cancelada no aviso do Windows.";
        }
    }

    /// <summary>Opens the hosts file in Notepad as administrator, so the person can edit it by hand.</summary>
    public static void OpenHostsInNotepad()
    {
        try
        {
            Process.Start(new ProcessStartInfo("notepad.exe", Path.Combine(Environment.SystemDirectory, "drivers", "etc", "hosts"))
            {
                UseShellExecute = true,
                Verb = "runas",
            });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == Cancelled)
        {
        }
    }

    /// <summary>The Helper's executable, from its service registration.</summary>
    private static string? HelperPath()
    {
        using var key = Registry.LocalMachine.OpenSubKey(ServiceKey);
        if (key?.GetValue("ImagePath") is not string imagePath)
            return null;
        imagePath = Environment.ExpandEnvironmentVariables(imagePath.Trim());
        // Quoted, or not: an unquoted path with spaces still ends at ".exe".
        var exe = imagePath.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        var path = imagePath.StartsWith('"') ? imagePath[1..imagePath.IndexOf('"', 1)]
            : exe > 0 ? imagePath[..(exe + 4)]
            : imagePath;
        return File.Exists(path) ? path : null;
    }
}
