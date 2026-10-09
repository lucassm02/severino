using System.Text;
using Severino.Core.Configuration;

namespace Severino.Tests.Configuration;

public sealed class ConfigStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "severino-tests", Guid.NewGuid().ToString("N"));
    private readonly SteppingTimeProvider _time = new();
    private readonly ConfigStore _store;

    public ConfigStoreTests() => _store = new ConfigStore(_dir, _time);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Load_without_file_returns_defaults_and_writes_nothing()
    {
        var result = _store.Load();

        Assert.Equal(ConfigLoadStatus.Defaults, result.Status);
        Assert.Equal(80, result.Config.Settings.HttpPort);
        Assert.Equal(".loc", result.Config.Settings.DefaultSuffix);
        Assert.False(Directory.Exists(_dir));
    }

    [Fact]
    public void Save_then_load_round_trips()
    {
        var config = new SeverinoConfig
        {
            Settings = new AppSettings { HttpPort = 8080, Theme = AppTheme.Dark, StartMinimized = true },
            State = new AppState { CloseToTrayHintShown = true },
            Routes = [new RouteEntry { Domain = "callfred.loc", Target = "http://127.0.0.1:3000", Https = true }],
        };

        _store.Save(config);
        var loaded = _store.Load();

        Assert.Equal(ConfigLoadStatus.Loaded, loaded.Status);
        Assert.Equal(8080, loaded.Config.Settings.HttpPort);
        Assert.Equal(AppTheme.Dark, loaded.Config.Settings.Theme);
        Assert.True(loaded.Config.Settings.StartMinimized);
        Assert.Equal(config.Settings.AllowedSuffixes, loaded.Config.Settings.AllowedSuffixes);
        Assert.Equal(config.State, loaded.Config.State);
        Assert.Equal(config.Routes.Single(), loaded.Config.Routes.Single());
    }

    [Fact]
    public void Save_writes_camel_case_utf8_without_bom()
    {
        _store.Save(new SeverinoConfig());

        var bytes = File.ReadAllBytes(_store.FilePath);
        Assert.NotEqual(0xEF, bytes[0]);
        var json = Encoding.UTF8.GetString(bytes);
        Assert.Contains("\"httpPort\": 80", json);
        Assert.Contains("\"theme\": \"auto\"", json);
    }

    [Fact]
    public void Save_keeps_only_the_last_five_backups()
    {
        for (var i = 0; i < 8; i++)
            _store.Save(new SeverinoConfig { Settings = new AppSettings { HttpPort = 8000 + i } });

        var backups = Directory.GetFiles(_store.BackupDirectory);
        Assert.Equal(ConfigStore.MaxBackups, backups.Length);
        // The newest backup is the file as it was before the last save.
        var newest = backups.Order(StringComparer.Ordinal).Last();
        Assert.Contains("\"httpPort\": 8006", File.ReadAllText(newest));
        Assert.Contains("\"httpPort\": 8007", File.ReadAllText(_store.FilePath));
    }

    [Fact]
    public void Save_leaves_no_temp_file_behind()
    {
        _store.Save(new SeverinoConfig());
        _store.Save(new SeverinoConfig());

        Assert.Equal([ConfigStore.FileName], Directory.GetFiles(_dir).Select(Path.GetFileName));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("{\"version\": 1, \"settings\": null}")]
    [InlineData("{\"version\": 0}")]
    [InlineData("")]
    public void Load_quarantines_unreadable_file_and_returns_defaults(string content)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(_store.FilePath, content);

        var result = _store.Load();

        Assert.Equal(ConfigLoadStatus.Recovered, result.Status);
        Assert.Equal(new AppSettings().HttpPort, result.Config.Settings.HttpPort);
        Assert.False(File.Exists(_store.FilePath));
        Assert.Equal(content, File.ReadAllText(result.QuarantinedPath!));
    }

    [Fact]
    public void Load_rejects_newer_version_without_touching_the_file()
    {
        Directory.CreateDirectory(_dir);
        const string content = "{\"version\": 99}";
        File.WriteAllText(_store.FilePath, content);

        var ex = Assert.Throws<UnsupportedConfigVersionException>(() => _store.Load());

        Assert.Equal(99, ex.Version);
        Assert.Equal(content, File.ReadAllText(_store.FilePath));
    }

    [Fact]
    public void Load_fills_missing_properties_with_defaults()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(_store.FilePath, "{\"version\": 1, \"settings\": {\"httpPort\": 8080}}");

        var result = _store.Load();

        Assert.Equal(ConfigLoadStatus.Loaded, result.Status);
        Assert.Equal(8080, result.Config.Settings.HttpPort);
        Assert.Equal(443, result.Config.Settings.HttpsPort);
        Assert.Empty(result.Config.Routes);
    }

    /// <summary>Advances one second per read so backup names never collide.</summary>
    private sealed class SteppingTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now = _now.AddSeconds(1);
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
