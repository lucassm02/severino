using System.Text.RegularExpressions;

namespace Severino.Core.Certificates;

/// <summary>
/// Whether Firefox trusts the roots in the Windows store, where the local CA lives. Firefox keeps
/// its own list, and imports the Windows one only with <c>security.enterprise_roots.enabled</c>:
/// on by default since Firefox 120, off before.
/// </summary>
public static partial class FirefoxTrust
{
    public const string Preference = "security.enterprise_roots.enabled";
    private const int DefaultOnSince = 120;

    public static string DefaultProfilesDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mozilla", "Firefox", "Profiles");

    /// <summary>
    /// Names of the profiles that would not trust the local CA, like "default-release". Empty when
    /// Firefox is not installed or every profile is fine.
    /// </summary>
    public static IReadOnlyList<string> ProfilesIgnoringWindowsRoots(string? profilesDirectory = null)
    {
        var root = profilesDirectory ?? DefaultProfilesDirectory;
        if (!Directory.Exists(root))
            return [];

        var result = new List<string>();
        foreach (var profile in Directory.EnumerateDirectories(root))
        {
            var prefs = ReadOrNull(Path.Combine(profile, "prefs.js"));
            if (prefs is null)
                continue; // never started
            var compatibility = ReadOrNull(Path.Combine(profile, "compatibility.ini"));
            if (!TrustsWindowsRoots(prefs, compatibility))
                result.Add(DisplayName(Path.GetFileName(profile)));
        }
        return result;
    }

    /// <summary>The preference when set; otherwise the default of the version that last ran the profile.</summary>
    public static bool TrustsWindowsRoots(string prefs, string? compatibility)
    {
        var match = PreferenceLine().Match(prefs);
        if (match.Success)
            return match.Groups[1].Value == "true";
        var version = VersionLine().Match(compatibility ?? "");
        // Unknown version: assume a current Firefox rather than warn about nothing.
        return !version.Success || int.Parse(version.Groups[1].Value) >= DefaultOnSince;
    }

    // Profile folders are "<random>.<name>", e.g. "x1y2z3.default-release".
    private static string DisplayName(string folder) => folder.IndexOf('.') is > 0 and var dot ? folder[(dot + 1)..] : folder;

    private static string? ReadOrNull(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (IOException)
        {
            return null; // Firefox holds it open while writing
        }
    }

    [GeneratedRegex("""user_pref\("security\.enterprise_roots\.enabled",\s*(true|false)\);""")]
    private static partial Regex PreferenceLine();

    [GeneratedRegex(@"^LastVersion=(\d+)\.", RegexOptions.Multiline)]
    private static partial Regex VersionLine();
}
