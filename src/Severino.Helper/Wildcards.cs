using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Severino.Contracts;

namespace Severino.Helper;

/// <summary>Severino's NRPT rules: one per suffix, pointing at the wildcard DNS server.</summary>
public interface INrptRules
{
    /// <summary>Leaves exactly these suffixes (".meuapp.sev") among the rules commented "Severino".</summary>
    void Apply(IReadOnlyCollection<string> namespaces);
}

/// <summary>
/// The wildcard names from both sources, with their lifetimes: route wildcards come and go with
/// the app (like the routes block), DNS-entry wildcards stay, saved next to the Helper's binary so
/// they come back after a restart. Only approved suffixes get an NRPT rule and answers.
/// </summary>
public sealed class Wildcards(WildcardTable table, INrptRules nrpt, IDnsApprovals approvals, ILogger<Wildcards> logger, string? statePath = null)
{
    private readonly Lock _gate = new();
    private readonly string _statePath = statePath ?? Path.Combine(AppContext.BaseDirectory, "wildcards.json");
    private IReadOnlyList<HostEntry> _routes = [];
    private IReadOnlyList<HostEntry> _dns = [];
    private IReadOnlyList<string>? _namespaces;

    /// <summary>The DNS-entry wildcards saved by a previous run.</summary>
    public void Load()
    {
        try
        {
            if (File.Exists(_statePath)
                && HelperProtocol.TryNormalizeDnsEntries(HelperProtocol.DeserializeRequest(File.ReadAllBytes(_statePath)).Entries, out var entries, out _))
                SetDns(entries);
            else
                lock (_gate) Apply();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not read the saved wildcards");
        }
    }

    /// <summary>Route wildcards (loopback). Returns the ones whose suffix waits for an approval.</summary>
    public IReadOnlyList<HostEntry> SetRoutes(IReadOnlyList<HostEntry> wildcards)
    {
        lock (_gate)
        {
            _routes = wildcards;
            return Apply().Where(wildcards.Contains).ToList();
        }
    }

    /// <summary>DNS-entry wildcards, saved for the next start. Returns the ones waiting for an approval.</summary>
    public IReadOnlyList<HostEntry> SetDns(IReadOnlyList<HostEntry> wildcards)
    {
        lock (_gate)
        {
            _dns = wildcards;
            try
            {
                if (wildcards.Count == 0)
                    File.Delete(_statePath);
                else
                    File.WriteAllBytes(_statePath, HelperProtocol.Serialize(HelperRequest.SyncDns(wildcards)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Could not save the wildcards");
            }
            return Apply().Where(wildcards.Contains).ToList();
        }
    }

    /// <summary>Drops everything: the table, the saved file and the NRPT rules. For uninstall.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _routes = [];
            SetDns([]);
        }
    }

    /// <summary>Answers and rules for the approved wildcards; returns the pending ones.</summary>
    private List<HostEntry> Apply()
    {
        var all = _routes.Concat(_dns).ToList();
        var pending = all.Where(e => !IsApproved(e)).ToList();
        var allowed = all.Except(pending).ToList();
        table.Set(allowed);

        var namespaces = allowed.Select(e => e.Name[1..]).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (_namespaces is null || !_namespaces.SequenceEqual(namespaces))
        {
            try
            {
                nrpt.Apply(namespaces);
                _namespaces = namespaces;
                logger.LogInformation("NRPT rules for {Namespaces}", string.Join(", ", namespaces));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not update the NRPT rules");
            }
        }
        return pending;
    }

    /// <summary>The suffix approved by an administrator, and a public address approved too.</summary>
    private bool IsApproved(HostEntry wildcard) =>
        approvals.IsApproved(new HostEntry(wildcard.Name, WildcardDnsAddress.Server))
        && DnsAddress.TryClassify(wildcard.Address, out _, out var scope)
        && (scope != AddressScope.Public || approvals.IsApproved(wildcard));
}

/// <summary>Runs the DnsClient cmdlets: the supported way to change NRPT rules and have Windows pick them up.</summary>
public sealed class PowerShellNrptRules(ILogger<PowerShellNrptRules> logger) : INrptRules
{
    public void Apply(IReadOnlyCollection<string> namespaces)
    {
        // The suffixes passed the domain validator: letters, digits, '-' and '.', safe in quotes.
        var wanted = string.Join(",", namespaces.Select(n => $"'{n}'"));
        var script =
            $"$want = @({wanted}); " +
            $"$have = @(Get-DnsClientNrptRule | Where-Object Comment -eq '{WildcardDnsAddress.NrptComment}'); " +
            "foreach ($r in $have) { if ($want -notcontains $r.Namespace[0]) { Remove-DnsClientNrptRule -Name $r.Name -Force } }; " +
            "foreach ($ns in $want) { if (-not ($have | Where-Object { $_.Namespace -contains $ns })) { " +
            $"Add-DnsClientNrptRule -Namespace $ns -NameServers {WildcardDnsAddress.Server} -Comment '{WildcardDnsAddress.NrptComment}' }} }}; " +
            "Clear-DnsClientCache";

        using var process = Process.Start(new ProcessStartInfo("powershell.exe")
        {
            ArgumentList = { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script },
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("powershell.exe did not start");
        var error = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(TimeSpan.FromSeconds(30)))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("The NRPT cmdlets did not finish in 30 s.");
        }
        if (process.ExitCode != 0 || error.Length > 0)
            logger.LogWarning("NRPT cmdlets exit {Code}: {Error}", process.ExitCode, error.Trim());
    }
}
