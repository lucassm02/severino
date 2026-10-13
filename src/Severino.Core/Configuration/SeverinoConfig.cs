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

    /// <summary>Plain name → address entries, kept in the hosts while the app is closed or paused.</summary>
    public IReadOnlyList<DnsEntry> DnsEntries { get; set; } = [];
}

/// <summary>A line of the hosts that Severino owns: one address and the names that point to it.</summary>
public sealed record DnsEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Normalized; one-label names allowed.</summary>
    public IReadOnlyList<string> Names { get; set; } = [];

    /// <summary>Any host address: loopback, private or public (public ones need an approval).</summary>
    public string Address { get; set; } = "";

    public bool Enabled { get; set; } = true;
    public string Notes { get; set; } = "";

    /// <summary>Counts only in this environment; empty for all of them (see <see cref="Environments"/>).</summary>
    public string Environment { get; set; } = "";

    /// <summary>The project file that brought it (see ProjectFiles); empty for one made here.</summary>
    public string Project { get; set; } = "";
}

public sealed record AppSettings
{
    public int HttpPort { get; set; } = 80;
    public int HttpsPort { get; set; } = 443;
    public bool StartWithWindows { get; set; }
    public bool StartMinimized { get; set; }
    public AppTheme Theme { get; set; } = AppTheme.Auto;

    /// <summary>The UI language; Auto follows the Windows display language.</summary>
    public AppLanguage Language { get; set; } = AppLanguage.Auto;

    /// <summary>WSL distros whose apps call the service routes by name; each gets the hosts block and the NAT rules.</summary>
    public IReadOnlyList<string> WslDistros { get; set; } = [];

    /// <summary>The chosen WSL distros trust the local CA, for curl and Node there (see WslCaScript).</summary>
    public bool WslTrustCa { get; set; }

    /// <summary>Refresh imported services on their own while the app is open (Docker events, Kubernetes every 30 s).</summary>
    public bool WatchServices { get; set; } = true;

    /// <summary>Ask GitHub once a day for a newer release and offer it.</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>The tray tells when a route or service with <c>NotifyHealth</c> stops answering, and when it is back.</summary>
    public bool HealthNotifications { get; set; } = true;

    /// <summary>The environment whose items count; empty for none, when only items without one do.</summary>
    public string ActiveEnvironment { get; set; } = "";
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

    /// <summary>When GitHub last answered about the latest release.</summary>
    public DateTimeOffset? LastUpdateCheck { get; set; }

    /// <summary>"0.5.0" after "Pular esta versão": no notice until a newer one comes out.</summary>
    public string SkippedVersion { get; set; } = "";

    /// <summary>The version the tray already announced, so it does it once.</summary>
    public string NotifiedVersion { get; set; } = "";

    /// <summary>Distros that got the local CA, so it can be taken out of them later, stopped ones included.</summary>
    public IReadOnlyList<string> WslTrustDistros { get; set; } = [];

    /// <summary>The project files applied here, for "Aplicar de novo".</summary>
    public IReadOnlyList<ProjectRef> Projects { get; set; } = [];
}

/// <summary>A project applied from a severino.json: its name, on its items, and where the file was.</summary>
public sealed record ProjectRef
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
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

    /// <summary>"/api" to take only that part of the domain; empty for all of it. The longest matching path wins.</summary>
    public string Path { get; set; } = "";

    /// <summary>Sends "/api/pedidos" to the target as "/pedidos". A folder always gets the path without the route's part.</summary>
    public bool StripPath { get; set; }

    /// <summary>A folder target: paths with no file get the folder's index.html, for single-page apps.</summary>
    public bool SpaFallback { get; set; }

    /// <summary>A label shared by routes switched on and off together, like "meuapp"; empty for none.</summary>
    public string Group { get; set; } = "";

    /// <summary>A tray notice when the target stops answering, and when it is back.</summary>
    public bool NotifyHealth { get; set; }

    /// <summary>CORS, headers, delay and simulated failure, applied by the proxy on the way.</summary>
    public TrafficRules Rules { get; set; } = new();

    /// <summary>Counts only in this environment; empty for all of them (see <see cref="Environments"/>).</summary>
    public string Environment { get; set; } = "";

    /// <summary>The project file that brought it (see ProjectFiles); empty for one made here.</summary>
    public string Project { get; set; } = "";
}

/// <summary>What the proxy does to a route's traffic beyond forwarding it. All off by default.</summary>
public sealed record TrafficRules
{
    /// <summary>Answers CORS preflights and lets the requesting origin read the answers, with credentials.</summary>
    public bool Cors { get; set; }

    /// <summary>Set on the way to the server; an empty value removes the header.</summary>
    public IReadOnlyList<HeaderRule> RequestHeaders { get; set; } = [];

    /// <summary>Set on the way back to the browser; an empty value removes the header.</summary>
    public IReadOnlyList<HeaderRule> ResponseHeaders { get; set; } = [];

    /// <summary>Waited before forwarding, up to 30 s.</summary>
    public int DelayMs { get; set; }

    /// <summary>The status a simulated failure answers with: 429, 500, 502, 503 or 504.</summary>
    public int FailStatus { get; set; } = 503;

    /// <summary>How many requests in a hundred fail on purpose; 0 for none.</summary>
    public int FailPercent { get; set; }

    public bool HasAny() => Cors || RequestHeaders.Count > 0 || ResponseHeaders.Count > 0 || DelayMs > 0 || FailPercent > 0;

    // The header lists by their contents, not by reference: a route read back from disk equals the one saved.
    public bool Equals(TrafficRules? other) =>
        other is not null && Cors == other.Cors && DelayMs == other.DelayMs && FailStatus == other.FailStatus && FailPercent == other.FailPercent
        && RequestHeaders.SequenceEqual(other.RequestHeaders) && ResponseHeaders.SequenceEqual(other.ResponseHeaders);

    public override int GetHashCode() => HashCode.Combine(Cors, DelayMs, FailStatus, FailPercent, RequestHeaders.Count, ResponseHeaders.Count);
}

public sealed record HeaderRule
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
}

/// <summary>
/// A service as the app knows it, by one or more names (catalogoapi, catalogoapi.staging, …),
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

    /// <summary>
    /// A ClusterIP-only Kubernetes service: Severino keeps a kubectl port-forward running where the
    /// origin's kubectl is, and the ports' targets are its local ports on 127.0.0.1.
    /// </summary>
    public bool PortForward { get; set; }

    public string Notes { get; set; } = "";

    /// <summary>A tray notice when the destination stops answering, and when it is back.</summary>
    public bool NotifyHealth { get; set; }

    /// <summary>Counts only in this environment; empty for all of them (see <see cref="Environments"/>).</summary>
    public string Environment { get; set; } = "";

    /// <summary>The project file that brought it (see ProjectFiles); empty for one made here.</summary>
    public string Project { get; set; } = "";
}

public sealed record ServicePort
{
    /// <summary>What the app calls, e.g. 80 for catalogoapi.</summary>
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

[JsonConverter(typeof(JsonStringEnumConverter<AppLanguage>))]
public enum AppLanguage
{
    [JsonStringEnumMemberName("auto")] Auto,
    [JsonStringEnumMemberName("en")] English,
    [JsonStringEnumMemberName("pt-BR")] Portuguese,
}

[JsonConverter(typeof(JsonStringEnumConverter<AppTheme>))]
public enum AppTheme
{
    [JsonStringEnumMemberName("auto")] Auto,
    [JsonStringEnumMemberName("light")] Light,
    [JsonStringEnumMemberName("dark")] Dark,
}
