using System.Security.Cryptography;

namespace Severino.Core.Updates;

/// <summary>
/// Downloads a release's setup and checks it against the SHA-256 GitHub published. A file that
/// does not match is deleted before anyone can run it.
/// </summary>
public sealed class UpdateDownloader(HttpClient http)
{
    public static string DefaultDirectory => Path.Combine(Path.GetTempPath(), "Severino");

    /// <summary>The path of the verified setup.</summary>
    /// <param name="progress">From 0 to 1; not reported when the size is unknown.</param>
    /// <exception cref="UpdateDownloadException">No digest to check against, or one that does not match.</exception>
    /// <exception cref="HttpRequestException">The download failed.</exception>
    public async Task<string> DownloadAsync(ReleaseInstaller installer, string directory, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (installer.Sha256 is null)
            throw new UpdateDownloadException("O GitHub não informou o SHA-256 deste instalador, então ele não pode ser conferido.");

        Directory.CreateDirectory(directory);
        // The name comes from the release; only its file name part is used.
        var path = Path.Combine(directory, Path.GetFileName(installer.Name));
        var partial = path + ".part";
        try
        {
            using var response = await http.GetAsync(installer.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? installer.Size;

            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var target = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    sha.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    done += read;
                    if (total > 0)
                        progress?.Report(Math.Min(1, (double)done / total));
                }
            }

            var actual = Convert.ToHexStringLower(sha.GetHashAndReset());
            if (actual != installer.Sha256)
                throw new UpdateDownloadException("O instalador baixado não confere com o SHA-256 publicado na release e foi apagado.");

            File.Move(partial, path, overwrite: true);
            return path;
        }
        finally
        {
            if (File.Exists(partial))
                File.Delete(partial);
        }
    }
}

public sealed class UpdateDownloadException(string message) : Exception(message);
