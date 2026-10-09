using System.Text.Json.Serialization;

namespace Severino.Core.Configuration;

// Properties use `set`, not `init`: the System.Text.Json source generator assigns init-only
// properties in an object initializer, so a key missing from the file would become default(T)
// instead of keeping the defaults below. Treat instances as immutable and change them with `with`.

public sealed record SeverinoConfig
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public AppSettings Settings { get; set; } = new();
    public AppState State { get; set; } = new();
    public IReadOnlyList<RouteEntry> Routes { get; set; } = [];
}

public sealed record AppSettings
{
    public int HttpPort { get; set; } = 80;
    public int HttpsPort { get; set; } = 443;
    public bool StartWithWindows { get; set; }
    public bool StartMinimized { get; set; }
    public AppTheme Theme { get; set; } = AppTheme.Auto;
}

/// <summary>Flags the app records about itself; not shown as settings.</summary>
public sealed record AppState
{
    public bool CloseToTrayHintShown { get; set; }
}

public sealed record RouteEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Domain { get; set; }
    public required string Target { get; set; }
    public bool Enabled { get; set; } = true;
    public bool Https { get; set; }
    public bool RedirectToHttps { get; set; }
    public bool PreserveHost { get; set; }
    public bool IgnoreTargetCertErrors { get; set; }
    public string Notes { get; set; } = "";
}

[JsonConverter(typeof(JsonStringEnumConverter<AppTheme>))]
public enum AppTheme
{
    [JsonStringEnumMemberName("auto")] Auto,
    [JsonStringEnumMemberName("light")] Light,
    [JsonStringEnumMemberName("dark")] Dark,
}
