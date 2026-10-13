using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Severino.Core.Routes;

/// <summary>
/// The secrets of a route's access, never kept as typed: a password becomes PBKDF2-SHA256 with a
/// salt of its own, slow on purpose; a header's value, usually a long random key, a SHA-256.
/// Comparisons take the same time whatever matches.
/// </summary>
public static class AccessSecrets
{
    private const string PasswordScheme = "pbkdf2-sha256";
    private const string KeyScheme = "sha256";
    private const int Iterations = 210_000;
    private const int SaltSize = 16;
    private const int HashSize = 32;

    public static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, HashSize);
        return string.Join('$', PasswordScheme, Iterations.ToString(CultureInfo.InvariantCulture), Convert.ToBase64String(salt), Convert.ToBase64String(hash));
    }

    public static bool VerifyPassword(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != PasswordScheme || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var iterations))
            return false;
        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static string HashKey(string value) => $"{KeyScheme}${Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))}";

    public static bool VerifyKey(string value, string stored) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(HashKey(value)), Encoding.ASCII.GetBytes(stored));

    public static bool IsPasswordHash(string? stored) => stored?.StartsWith(PasswordScheme + "$", StringComparison.Ordinal) == true;

    public static bool IsKeyHash(string? stored) => stored?.StartsWith(KeyScheme + "$", StringComparison.Ordinal) == true;
}
