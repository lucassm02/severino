using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Severino.Contracts;

/// <summary>
/// Validates and normalizes the domain of a route. The app and the Helper both run every name
/// through here, so the Helper never trusts what arrives over the pipe.
/// </summary>
public static class DomainName
{
    public const int MaxLength = 253;
    public const int MaxLabelLength = 63;

    private static readonly IdnMapping Idn = new() { UseStd3AsciiRules = true };

    /// <summary>
    /// Returns the lowercase ASCII (punycode) form of <paramref name="input"/>, or a reason in
    /// Portuguese for the user when it is not an acceptable name.
    /// </summary>
    /// <param name="allowSingleLabel">
    /// For service routes, whose names come from a cluster or a Compose file ("redis",
    /// "algarbffapi"). Web routes keep at least two labels.
    /// </param>
    /// <summary>
    /// A wildcard like <c>*.meuapp.sev</c>: any name below the base, at any depth. The base
    /// needs two labels or more, so a whole TLD (<c>*.com</c>) is never one.
    /// </summary>
    public static bool TryNormalizeWildcard(
        string? input,
        [NotNullWhen(true)] out string? normalized,
        [NotNullWhen(false)] out string? error)
    {
        normalized = null;
        var name = (input ?? "").Trim();
        if (!name.StartsWith("*.", StringComparison.Ordinal))
            return Fail("Um curinga começa com *., como *.meuapp.sev.", out error);
        if (!TryNormalize(name[2..], out var baseName, out error))
            return false;
        normalized = "*." + baseName;
        return true;
    }

    public static bool IsWildcard(string? name) => name?.TrimStart().StartsWith("*.", StringComparison.Ordinal) == true;

    /// <summary>Whether <paramref name="name"/> is below <paramref name="wildcard"/>'s base (both normalized).</summary>
    public static bool MatchesWildcard(string wildcard, string name) =>
        name.EndsWith(wildcard[1..], StringComparison.Ordinal) && name.Length > wildcard.Length - 1;

    public static bool TryNormalize(
        string? input,
        [NotNullWhen(true)] out string? normalized,
        [NotNullWhen(false)] out string? error,
        bool allowSingleLabel = false)
    {
        normalized = null;
        var name = (input ?? "").Trim(' ').TrimEnd('.');

        if (name.Length == 0)
            return Fail("Informe um domínio.", out error);

        // Checked before IDN conversion so the message is specific, and so nothing that could
        // break a hosts line (newline, space, '#') ever reaches the conversion.
        if (name.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || c == '#'))
            return Fail("O domínio não pode ter espaços, quebras de linha ou '#'.", out error);

        string ascii;
        try
        {
            ascii = Idn.GetAscii(name).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            return Fail("O domínio tem caracteres inválidos.", out error);
        }

        if (ascii.Length > MaxLength)
            return Fail($"O domínio passa de {MaxLength} caracteres.", out error);

        var labels = ascii.Split('.');
        if (labels.Length < 2 && !allowSingleLabel)
            return Fail("Use pelo menos duas partes, como meuapp.sev.", out error);

        foreach (var label in labels)
        {
            if (label.Length is 0 or > MaxLabelLength)
                return Fail($"Cada parte do domínio deve ter de 1 a {MaxLabelLength} caracteres.", out error);
            if (label[0] == '-' || label[^1] == '-')
                return Fail("Uma parte do domínio não pode começar nem terminar com hífen.", out error);
            if (!label.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-'))
                return Fail("Use só letras, números e hífen.", out error);
        }

        if (labels[^1].All(char.IsAsciiDigit))
            return Fail("O domínio não pode terminar em número; para endereços IP não é preciso rota.", out error);

        if (ascii == "localhost")
            return Fail("localhost já aponta para esta máquina.", out error);

        normalized = ascii;
        error = null;
        return true;
    }

    public static bool IsValid(string? input) => TryNormalize(input, out _, out _);

    private static bool Fail(string message, out string error)
    {
        error = message;
        return false;
    }
}
