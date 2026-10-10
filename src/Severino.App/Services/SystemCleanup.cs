using System.IO;
using Severino.Proxy.Certificates;

namespace Severino.App.Services;

/// <param name="CaRemoved">False when the user kept the root in Windows; its key is gone either way.</param>
public sealed record CleanupResult(bool CaRemoved);

/// <summary>
/// Undoes what Severino changed in Windows for this user: the local CA and the autostart entry.
/// Used by "Limpar tudo" and by <c>Severino.exe --cleanup</c>, which the uninstaller runs. The
/// hosts block is not here: only the Helper can write it, and it clears it on exit or with
/// <c>Severino.Helper.exe --clear-hosts</c>.
/// </summary>
public sealed class SystemCleanup(LocalCa ca, AutoStart autoStart)
{
    /// <summary>Removing the CA shows Windows' warning; this blocks until it is answered.</summary>
    public CleanupResult Run()
    {
        var caRemoved = WindowsPrompt.Run(ca.Remove);
        autoStart.Disable();
        return new(caRemoved);
    }

    /// <summary>
    /// Deletes the config, backups, CA files and logs. Best effort: a log file still held open is
    /// left behind rather than failing the exit.
    /// </summary>
    public static void DeleteData(string directory)
    {
        if (!Directory.Exists(directory))
            return;
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
