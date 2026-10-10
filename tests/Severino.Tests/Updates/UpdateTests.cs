using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Severino.Core.Configuration;
using Severino.Core.Updates;

namespace Severino.Tests.Updates;

public sealed class UpdateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "severino-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private const string Digest = "0af47a6256c80a25719496234845533cde8e3218f78a9f272831fe718d2235e3";

    // The shape GitHub returns for /releases/latest, cut to what the app reads.
    private static string Release(string tag = "v0.5.0", string? digest = "sha256:" + Digest, string asset = "Severino-Setup-0.5.0.exe") => $$"""
        {
          "tag_name": "{{tag}}",
          "html_url": "https://github.com/lucassm02/severino/releases/tag/{{tag}}",
          "prerelease": false,
          "assets": [
            { "name": "notes.txt", "browser_download_url": "https://github.com/x/notes.txt", "size": 10 },
            { "name": "{{asset}}", "size": 81254777,
              "browser_download_url": "https://github.com/lucassm02/severino/releases/download/{{tag}}/{{asset}}"
              {{(digest is null ? "" : $", \"digest\": \"{digest}\"")}} }
          ]
        }
        """;

    [Fact]
    public void Parse_reads_version_page_and_the_exe()
    {
        var release = UpdateChecker.Parse(JsonDocument.Parse(Release()));

        Assert.Equal(new Version(0, 5, 0), release.Version);
        Assert.Equal("https://github.com/lucassm02/severino/releases/tag/v0.5.0", release.Page.AbsoluteUri);
        Assert.NotNull(release.Installer);
        Assert.Equal("Severino-Setup-0.5.0.exe", release.Installer.Name);
        Assert.Equal(81254777, release.Installer.Size);
        Assert.Equal(Digest, release.Installer.Sha256);
    }

    [Fact]
    public void Parse_without_digest_keeps_the_installer_but_no_hash()
    {
        var release = UpdateChecker.Parse(JsonDocument.Parse(Release(digest: null)));
        Assert.NotNull(release.Installer);
        Assert.Null(release.Installer.Sha256);
    }

    [Theory]
    [InlineData("md5:0123")]
    [InlineData("sha256:not-hex")]
    public void Parse_ignores_a_digest_it_cannot_check(string digest) =>
        Assert.Null(UpdateChecker.Parse(JsonDocument.Parse(Release(digest: digest))).Installer!.Sha256);

    [Fact]
    public void Parse_without_exe_has_no_installer() =>
        Assert.Null(UpdateChecker.Parse(JsonDocument.Parse(Release(asset: "Severino.zip"))).Installer);

    [Fact]
    public void Parse_refuses_a_tag_that_is_not_a_version() =>
        Assert.Throws<UpdateCheckException>(() => UpdateChecker.Parse(JsonDocument.Parse(Release(tag: "nightly"))));

    [Theory]
    [InlineData("v0.5.0", "0.5.0")]
    [InlineData("0.5", "0.5.0")]
    [InlineData("V1.2.3", "1.2.3")]
    [InlineData("1.2.3.4", "1.2.3")]
    public void ParseVersion_keeps_three_numbers(string tag, string expected) =>
        Assert.Equal(Version.Parse(expected), UpdateChecker.ParseVersion(tag));

    [Theory]
    [InlineData("")]
    [InlineData("v")]
    [InlineData("v0.5.0-beta")]
    public void ParseVersion_rejects_the_rest(string tag) => Assert.Null(UpdateChecker.ParseVersion(tag));

    [Fact]
    public async Task GetLatest_sends_a_user_agent_and_compares_with_the_app()
    {
        HttpRequestMessage? sent = null;
        var http = new HttpClient(new FakeHandler(request =>
        {
            sent = request;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Release()) };
        }));
        var checker = new UpdateChecker(http, new Version(0, 4, 0, 0));

        var latest = await checker.GetLatestAsync();

        Assert.Equal(UpdateChecker.LatestRelease, sent!.RequestUri);
        Assert.Equal("Severino/0.4.0", sent.Headers.UserAgent.ToString());
        Assert.True(checker.IsNewer(latest));
        Assert.False(new UpdateChecker(http, new Version(0, 5, 0)).IsNewer(latest));
    }

    [Fact]
    public async Task GetLatest_fails_on_an_error_status()
    {
        var http = new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)));
        await Assert.ThrowsAsync<HttpRequestException>(() => new UpdateChecker(http, new Version(0, 4, 0)).GetLatestAsync());
    }

    [Fact]
    public void IsDue_after_a_day_or_never()
    {
        var now = new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
        Assert.True(UpdatePolicy.IsDue(null, now));
        Assert.False(UpdatePolicy.IsDue(now.AddHours(-23), now));
        Assert.True(UpdatePolicy.IsDue(now.AddHours(-24), now));
        Assert.True(UpdatePolicy.IsDue(now.AddDays(2), now)); // the clock went backwards
    }

    [Fact]
    public void ShouldOffer_respects_the_skipped_version()
    {
        var release = UpdateChecker.Parse(JsonDocument.Parse(Release("v0.5.0")));
        var current = new Version(0, 4, 0);

        Assert.True(UpdatePolicy.ShouldOffer(release, current, ""));
        Assert.False(UpdatePolicy.ShouldOffer(release, current, "0.5.0"));
        Assert.True(UpdatePolicy.ShouldOffer(release, current, "0.4.5"));
        Assert.False(UpdatePolicy.ShouldOffer(release, new Version(0, 5, 0), ""));
    }

    [Fact]
    public async Task Download_keeps_a_file_that_matches()
    {
        var bytes = Encoding.UTF8.GetBytes("um instalador de mentira");
        var installer = Installer(Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length);
        var reported = new List<double>();

        var path = await new UpdateDownloader(Serving(bytes)).DownloadAsync(installer, _dir, new SyncProgress(reported.Add));

        Assert.Equal(Path.Combine(_dir, "Severino-Setup-0.5.0.exe"), path);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(1, reported[^1]);
        Assert.Single(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task Download_deletes_a_file_that_does_not_match()
    {
        var bytes = Encoding.UTF8.GetBytes("adulterado");
        var installer = Installer(Digest, bytes.Length);

        await Assert.ThrowsAsync<UpdateDownloadException>(() => new UpdateDownloader(Serving(bytes)).DownloadAsync(installer, _dir));

        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task Download_without_digest_does_not_start()
    {
        var calls = 0;
        var http = new HttpClient(new FakeHandler(_ => { calls++; return new HttpResponseMessage(HttpStatusCode.OK); }));

        await Assert.ThrowsAsync<UpdateDownloadException>(() => new UpdateDownloader(http).DownloadAsync(Installer(null, 1), _dir));

        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Download_uses_only_the_file_name_of_the_asset()
    {
        var bytes = new byte[] { 1, 2, 3 };
        var installer = Installer(Convert.ToHexStringLower(SHA256.HashData(bytes)), 3) with { Name = @"..\..\evil.exe" };

        var path = await new UpdateDownloader(Serving(bytes)).DownloadAsync(installer, _dir);

        Assert.Equal(Path.Combine(_dir, "evil.exe"), path);
    }

    [Fact]
    public void Config_defaults_check_for_updates()
    {
        var config = new SeverinoConfig();
        Assert.True(config.Settings.CheckForUpdates);
        Assert.Null(config.State.LastUpdateCheck);
        Assert.Equal("", config.State.SkippedVersion);
    }

    private static ReleaseInstaller Installer(string? sha256, long size) =>
        new("Severino-Setup-0.5.0.exe", new Uri("https://github.com/lucassm02/severino/releases/download/v0.5.0/Severino-Setup-0.5.0.exe"), size, sha256);

    private static HttpClient Serving(byte[] bytes) =>
        new(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    // Progress<T> posts to the thread pool; this one reports in order, on the spot.
    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
