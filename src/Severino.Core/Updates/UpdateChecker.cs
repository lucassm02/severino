using System.Net.Http.Headers;
using System.Text.Json;

namespace Severino.Core.Updates;

/// <summary>A release on GitHub, newer or not than the running app.</summary>
public sealed record ReleaseInfo(Version Version, string Tag, Uri Page, ReleaseInstaller? Installer);

/// <summary>The setup attached to a release; <see cref="Sha256"/> is null when GitHub gave no digest.</summary>
public sealed record ReleaseInstaller(string Name, Uri Url, long Size, string? Sha256);

/// <summary>Asks GitHub for the latest release. Drafts and pre-releases never come back from that endpoint.</summary>
public sealed class UpdateChecker(HttpClient http, Version current)
{
    public static readonly Uri LatestRelease = new("https://api.github.com/repos/lucassm02/severino/releases/latest");

    public Version Current { get; } = Normalize(current);

    /// <exception cref="HttpRequestException">No answer, or not a 200.</exception>
    /// <exception cref="UpdateCheckException">An answer that is not a release.</exception>
    public async Task<ReleaseInfo> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestRelease);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Severino", Current.ToString(3)));
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        return Parse(await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken));
    }

    /// <summary>True when <paramref name="release"/> is newer than the running app.</summary>
    public bool IsNewer(ReleaseInfo release) => release.Version > Current;

    public static ReleaseInfo Parse(JsonDocument document)
    {
        var root = document.RootElement;
        var tag = String(root, "tag_name") ?? throw new UpdateCheckException("The answer has no tag_name.");
        var version = ParseVersion(tag) ?? throw new UpdateCheckException($"Tag {tag} is not a version.");
        var page = Uri.TryCreate(String(root, "html_url"), UriKind.Absolute, out var url) ? url
            : throw new UpdateCheckException("The answer has no html_url.");

        ReleaseInstaller? installer = null;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = String(asset, "name");
                if (name is null || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    || !Uri.TryCreate(String(asset, "browser_download_url"), UriKind.Absolute, out var download)
                    || download.Scheme != Uri.UriSchemeHttps)
                    continue;
                var size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var n) ? n : 0;
                installer = new ReleaseInstaller(name, download, size, Sha256(String(asset, "digest")));
                break;
            }
        }
        return new ReleaseInfo(version, tag, page, installer);
    }

    /// <summary>"v0.5.0" or "0.5.0" to 0.5.0; null for anything else.</summary>
    public static Version? ParseVersion(string tag)
    {
        var text = tag.Trim().TrimStart('v', 'V');
        return Version.TryParse(text, out var version) && text.Count(c => c == '.') <= 3 ? Normalize(version) : null;
    }

    // Three numbers, so 0.5 and 0.5.0 compare equal and a fourth number never decides.
    private static Version Normalize(Version v) => new(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0));

    /// <summary>"sha256:abc…" to "abc…" in lowercase; null for no digest or another algorithm.</summary>
    private static string? Sha256(string? digest)
    {
        const string prefix = "sha256:";
        if (digest is null || !digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;
        var hex = digest[prefix.Length..].Trim().ToLowerInvariant();
        return hex.Length == 64 && hex.All(Uri.IsHexDigit) ? hex : null;
    }

    private static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

public sealed class UpdateCheckException(string message) : Exception(message);
