using System.Net;
using System.Net.Sockets;
using System.Text;
using Severino.Core.Configuration;
using Severino.Core.Discovery;

namespace Severino.Core.Wsl;

/// <summary>
/// Shell scripts, run as root inside a WSL distro, that let apps there call service routes by
/// name. The distro does the same translation the Severino does on Windows, with its own kernel:
/// a block in <c>/etc/hosts</c> sends the names to the route's 127.77 address, and NAT rules send
/// each port of that address to the real destination. Nothing keeps running in the distro.
/// Both scripts are idempotent.
/// </summary>
public static class WslCallerScript
{
    /// <summary>Same markers as the Windows hosts block.</summary>
    public const string StartMarker = "# >>> Severino managed block (do not edit)";
    public const string EndMarker = "# <<< Severino";

    /// <summary>The nat chain hooked to OUTPUT, one DNAT per port.</summary>
    public const string Chain = "SEVERINO";

    /// <summary>The nat chain hooked to POSTROUTING, with the MASQUERADE for 127/8 leaving the distro.</summary>
    public const string PostChain = "SEVERINO-POST";

    /// <summary>Where the script keeps route_localnet's value before Severino turned it on. /run is gone after a restart, like the rules.</summary>
    public const string SavedLocalnet = "/run/severino-route_localnet";

    /// <summary>Exit code when the script ran but some destination could not be resolved in the distro.</summary>
    public const int PartialExitCode = 4;

    /// <summary>
    /// Whether apps in a WSL distro can reach the route's destination. Not when it is the Windows
    /// loopback: a container published by Docker on Windows, or a route made by hand pointing at
    /// 127.0.0.1. In NAT mode the distro does not see the Windows loopback.
    /// </summary>
    public static bool Reachable(ServiceRoute route)
    {
        var fromWsl = route.Origin is { } origin && CommandSource.FromId(origin.Source).IsWsl;
        return fromWsl || route.Ports.Any(p => !IsLoopback(p.TargetHost));
    }

    /// <summary>The script that makes the distro hold exactly <paramref name="services"/>.</summary>
    public static string Apply(IReadOnlyList<ServiceRoute> services)
    {
        var routes = services.Where(s => s.Enabled && Reachable(s)).ToList();
        if (routes.Count == 0)
            return Remove();

        var script = new StringBuilder(Preamble);
        script.Append("command -v iptables >/dev/null 2>&1 || fail 'O iptables não está instalado na distro.'\n");
        script.Append(WriteHosts(routes));

        // Remember route_localnet as it was, once per boot, so Remove can put it back.
        script.Append($"[ -e {SavedLocalnet} ] || cat /proc/sys/net/ipv4/conf/all/route_localnet > {SavedLocalnet}\n");
        script.Append("sysctl -qw net.ipv4.conf.all.route_localnet=1 || fail 'Não deu para ligar o route_localnet.'\n");
        foreach (var (chain, hook) in new[] { (Chain, "OUTPUT"), (PostChain, "POSTROUTING") })
        {
            script.Append($"iptables -w -t nat -N {chain} >/dev/null 2>&1\n");
            script.Append($"iptables -w -t nat -F {chain} || fail 'O iptables recusou a tabela nat.'\n");
            // -C prints the rule it finds on the nft backend.
            script.Append($"iptables -w -t nat -C {hook} -j {chain} >/dev/null 2>&1 || iptables -w -t nat -A {hook} -j {chain}\n");
        }
        // Without it the destination gets a packet from 127.0.0.1 and drops it.
        script.Append($"iptables -w -t nat -A {PostChain} -s 127.0.0.0/8 ! -o lo -j MASQUERADE\n");

        script.Append("""
            missing=0
            dnat() {
              to=$3
              case "$to" in *[!0-9.]*) to=$(getent ahostsv4 "$3" | awk 'NR==1 {print $1}');; esac
              if [ -z "$to" ]; then echo "Não resolvi $3 na distro." >&2; missing=1; return; fi
              iptables -w -t nat -A SEVERINO -d "$1/32" -p tcp --dport "$2" -j DNAT --to-destination "$to:$4"
            }

            """.Replace("\r\n", "\n"));
        foreach (var route in routes)
        {
            foreach (var port in route.Ports.Where(p => !IsIPv6(p.TargetHost)))
                script.Append($"dnat {CommandRunner.ShellQuote(route.Address)} {port.Port} {CommandRunner.ShellQuote(port.TargetHost)} {port.TargetPort}\n");
        }
        script.Append($"[ $missing = 0 ] || exit {PartialExitCode}\n");
        return script.ToString();
    }

    /// <summary>Takes out the block, the chains and route_localnet, as they were before.</summary>
    public static string Remove()
    {
        var script = new StringBuilder(Preamble);
        script.Append(WriteHosts([]));
        script.Append("if command -v iptables >/dev/null 2>&1; then\n");
        foreach (var (chain, hook) in new[] { (Chain, "OUTPUT"), (PostChain, "POSTROUTING") })
        {
            script.Append($"  while iptables -w -t nat -D {hook} -j {chain} 2>/dev/null; do :; done\n");
            script.Append($"  iptables -w -t nat -F {chain} 2>/dev/null; iptables -w -t nat -X {chain} 2>/dev/null\n");
        }
        script.Append("fi\n");
        script.Append($"if [ -e {SavedLocalnet} ]; then sysctl -qw net.ipv4.conf.all.route_localnet=$(cat {SavedLocalnet}); rm -f {SavedLocalnet}; fi\n");
        script.Append("exit 0\n");
        return script.ToString();
    }

    private const string Preamble = "set -u\nfail() { echo \"$1\" >&2; exit 3; }\n";

    /// <summary>
    /// Rewrites /etc/hosts in place (same inode, as WSL may have it bind-mounted) with the block
    /// where the old one was, or at the end. Other lines are kept byte for byte.
    /// </summary>
    private static string WriteHosts(IReadOnlyList<ServiceRoute> routes)
    {
        var block = new StringBuilder();
        if (routes.Count > 0)
        {
            block.Append(StartMarker).Append('\n');
            foreach (var route in routes)
            {
                foreach (var name in route.Names)
                    block.Append(route.Address.PadRight(Math.Max(11, route.Address.Length + 2))).Append(name).Append('\n');
            }
            block.Append(EndMarker).Append('\n');
        }

        return $$"""
            block=$(mktemp) && hosts=$(mktemp) || fail 'Não deu para criar arquivos temporários.'
            cat > "$block" <<'SEVERINO_BLOCK'
            {{block}}SEVERINO_BLOCK
            awk -v blockfile="$block" '
              BEGIN { while ((getline line < blockfile) > 0) text = text line "\n" }
              /^[ \t]*# >>> Severino/ { if (!done) { printf "%s", text; done = 1 } inblock = 1; next }
              inblock { if ($0 ~ /^[ \t]*# <<< Severino/) inblock = 0; next }
              { print }
              END { if (!done) printf "%s", text }
            ' /etc/hosts > "$hosts" || fail 'Não deu para ler o /etc/hosts.'
            cat "$hosts" > /etc/hosts || fail 'Não deu para gravar o /etc/hosts.'
            rm -f "$block" "$hosts"

            """.Replace("\r\n", "\n");
    }

    private static bool IsLoopback(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip));

    private static bool IsIPv6(string host) =>
        IPAddress.TryParse(host, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6;
}
