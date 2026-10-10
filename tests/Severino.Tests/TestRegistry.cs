using Microsoft.Win32;

namespace Severino.Tests;

/// <summary>Throwaway keys under HKCU\Software\Severino\Tests, so tests never touch the real settings.</summary>
internal static class TestRegistry
{
    private const string Root = @"Software\Severino\Tests";

    public static string NewKey() => $@"{Root}\{Guid.NewGuid():N}";

    /// <summary>Deletes <paramref name="key"/> and, when nothing else is left, its empty parents.</summary>
    public static void Delete(string key)
    {
        Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
        foreach (var parent in new[] { Root, @"Software\Severino" })
        {
            try
            {
                using (var open = Registry.CurrentUser.OpenSubKey(parent))
                {
                    if (open is not { SubKeyCount: 0, ValueCount: 0 })
                        return;
                }
                Registry.CurrentUser.DeleteSubKey(parent, throwOnMissingSubKey: false);
            }
            catch (InvalidOperationException)
            {
                return; // another test class just created a key under it, in parallel
            }
        }
    }
}
