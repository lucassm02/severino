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

    /// <summary>Service routes: names forwarded as plain TCP, usually imported from Kubernetes or Docker.</summary>
    public IReadOnlyList<ServiceRoute> Services { get; set; } = [];
}

public sealed record AppSettings
{
    public int HttpPort { get; set; } = 80;
    public int HttpsPort { get; set; } = 443;
    public bool StartWithWindows { get; set; }
    public bool StartMinimized { get; set; }
    public AppTheme Theme { get; set; } = AppTheme.Auto;

    /// <summary>WSL distros whose apps call the service routes by name; each gets the hosts block and the NAT rules.</summary>
    public IReadOnlyList<string> WslDistros { get; set; } = [];
}

/// <summary>Flags the app records about itself; not shown as settings.</summary>
public sealed record AppState
{
    public bool CloseToTrayHintShown { get; set; }

    /// <summary>The first-run wizard was finished or skipped; it can be reopened from Settings.</summary>
    public bool FirstRunCompleted { get; set; }

    /// <summary>TLD -> exists on the internet, cached for the CA coverage (see TldDirectory).</summary>
    public IReadOnlyDictionary<string, bool> TldExists { get; set; } = new Dictionary<string, bool>();

    /// <summary>Entries Severino added to the Windows proxy bypass list, so cleanup removes these and only these.</summary>
    public IReadOnlyList<string> ProxyBypassAdded { get; set; } = [];

    /// <summary>Which tools "Importar serviços" asks, as last left.</summary>
    public bool ImportKubernetes { get; set; } = true;

    public bool ImportDocker { get; set; } = true;
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

/// <summary>
/// A service as the app knows it, by one or more names (algarbffapi, algarbffapi.staging, …),
/// reached through a loopback address of its own. Each port is forwarded byte for byte, so the
/// name the app used, the HTTP Host or TLS SNI, arrives unchanged, and any protocol works.
/// </summary>
public sealed record ServiceRoute
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Normalized; one-label names allowed.</summary>
    public IReadOnlyList<string> Names { get; set; } = [];

    /// <summary>In 127.77.0.0/16, given once and kept, so the hosts block and the WSL rules stay put.</summary>
    public string Address { get; set; } = "";

    public IReadOnlyList<ServicePort> Ports { get; set; } = [];
    public bool Enabled { get; set; } = true;

    /// <summary>Where it was imported from, for "Atualizar"; null for one made by hand.</summary>
    public ServiceOrigin? Origin { get; set; }

    public string Notes { get; set; } = "";
}

public sealed record ServicePort
{
    /// <summary>What the app calls, e.g. 80 for algarbffapi.</summary>
    public int Port { get; set; }

    /// <summary>Where it really is: a node IP for a NodePort, 127.0.0.1 for a published container port.</summary>
    public string TargetHost { get; set; } = "";

    public int TargetPort { get; set; }

    public override string ToString() => $"{Port} → {TargetHost}:{TargetPort}";
}

public sealed record ServiceOrigin
{
    public ServiceKind Kind { get; set; }

    /// <summary>Where the tool ran: "windows" or "wsl:Ubuntu-22.04".</summary>
    public string Source { get; set; } = "";

    /// <summary>The kubectl context, or the Docker engine ID.</summary>
    public string Context { get; set; } = "";

    /// <summary>Kubernetes namespace, or Compose project.</summary>
    public string Namespace { get; set; } = "";

    /// <summary>Service or Compose service name, as the source calls it.</summary>
    public string Name { get; set; } = "";
}

[JsonConverter(typeof(JsonStringEnumConverter<ServiceKind>))]
public enum ServiceKind
{
    [JsonStringEnumMemberName("kubernetes")] Kubernetes,
    [JsonStringEnumMemberName("docker")] Docker,
}

[JsonConverter(typeof(JsonStringEnumConverter<AppTheme>))]
public enum AppTheme
{
    [JsonStringEnumMemberName("auto")] Auto,
    [JsonStringEnumMemberName("light")] Light,
    [JsonStringEnumMemberName("dark")] Dark,
}
