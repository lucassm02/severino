using System.Security.Cryptography;

namespace Severino.Proxy;

/// <summary>
/// The key that signs the login cookies of routes with a password, kept beside the config under
/// DPAPI, so sessions survive a restart of the app and no other Windows user can forge one.
/// </summary>
public static class SessionKey
{
    public const string FileName = "session.key";
    private const int Size = 32;
    private static readonly byte[] Entropy = "Severino.Sessions.v1"u8.ToArray();

    /// <summary>The key in <paramref name="directory"/>, made the first time; one of this run only when the file cannot be used.</summary>
    public static byte[] LoadOrCreate(string directory)
    {
        var path = Path.Combine(directory, FileName);
        try
        {
            if (File.Exists(path) && ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser) is { Length: Size } key)
                return key;
        }
        catch (CryptographicException)
        {
            // Another user's or another machine's file: a new key replaces it.
        }

        var fresh = RandomNumberGenerator.GetBytes(Size);
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(path, ProtectedData.Protect(fresh, Entropy, DataProtectionScope.CurrentUser));
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        return fresh;
    }
}
