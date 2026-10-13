namespace Severino.Core.Wsl;

/// <summary>
/// Shell scripts, run as root inside a WSL distro, that make the distro trust Severino's local CA:
/// the public certificate in the system store (update-ca-certificates on Debian, Ubuntu and Alpine;
/// update-ca-trust on Fedora, RHEL and Arch) and NODE_EXTRA_CA_CERTS for Node, which keeps its own
/// list. Unlike the hosts block and the NAT rules, this outlives a restart of the distro. Both
/// scripts are idempotent.
/// </summary>
public static class WslCaScript
{
    public const string FileName = "severino.crt";
    public const string ProfileScript = "/etc/profile.d/severino-ca.sh";

    private static readonly string[] Stores = ["/usr/local/share/ca-certificates", "/etc/pki/ca-trust/source/anchors"];

    /// <summary>Puts <paramref name="pem"/> (the root, without its key) in the distro's store.</summary>
    public static string Install(string pem)
    {
        var certificate = pem.Replace("\r\n", "\n").Trim();
        if (!certificate.StartsWith("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal) || certificate.Contains("PRIVATE KEY", StringComparison.Ordinal))
            throw new ArgumentException("Only a public certificate goes into a distro.", nameof(pem));
        return $$"""
            set -u
            fail() { echo "$1" >&2; exit 3; }
            if command -v update-ca-certificates >/dev/null 2>&1; then
              dir={{Stores[0]}}; update='update-ca-certificates'
            elif command -v update-ca-trust >/dev/null 2>&1; then
              dir={{Stores[1]}}; update='update-ca-trust extract'
            else
              fail 'The distro has neither update-ca-certificates nor update-ca-trust (the ca-certificates package).'
            fi
            mkdir -p "$dir" || fail "Could not create $dir."
            cat > "$dir/{{FileName}}" <<'SEVERINO_CA' || fail "Could not write $dir/{{FileName}}."
            {{certificate}}
            SEVERINO_CA
            $update >/dev/null 2>&1 || fail "$update failed."
            printf 'export NODE_EXTRA_CA_CERTS=%s\n' "$dir/{{FileName}}" > {{ProfileScript}} || fail 'Could not write {{ProfileScript}}.'
            exit 0

            """.Replace("\r\n", "\n");
    }

    /// <summary>Takes the certificate out of both stores, and the Node variable.</summary>
    public static string Remove() => $$"""
        set -u
        changed=0
        for file in {{Stores[0]}}/{{FileName}} {{Stores[1]}}/{{FileName}}; do
          if [ -e "$file" ]; then rm -f "$file"; changed=1; fi
        done
        rm -f {{ProfileScript}}
        if [ $changed = 1 ]; then
          if command -v update-ca-certificates >/dev/null 2>&1; then update-ca-certificates --fresh >/dev/null 2>&1; fi
          if command -v update-ca-trust >/dev/null 2>&1; then update-ca-trust extract >/dev/null 2>&1; fi
        fi
        exit 0

        """.Replace("\r\n", "\n");
}
