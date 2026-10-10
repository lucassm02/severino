using System.Text.Json;
using Microsoft.Extensions.Logging;
using Severino.Contracts;

namespace Severino.Helper;

/// <summary>Turns one pipe message into one response. Trusts nothing in the message.</summary>
public sealed class HelperRequestHandler(IHostsWriter hosts, IDnsApprovals approvals, ILogger<HelperRequestHandler> logger, Func<DateTime>? now = null)
{
    public static string HelperVersion { get; } =
        typeof(HelperRequestHandler).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private readonly Func<DateTime> _now = now ?? (() => DateTime.Now);

    public HelperResponse Handle(ReadOnlySpan<byte> message)
    {
        HelperRequest request;
        try
        {
            request = HelperProtocol.DeserializeRequest(message);
        }
        catch (JsonException)
        {
            return Fail("Mensagem inválida.");
        }

        switch (request.Command)
        {
            case HelperProtocol.PingCommand:
                return HelperResponse.Success(HelperVersion);

            case HelperProtocol.SyncCommand:
            {
                if (!HelperProtocol.TryNormalizeEntries(request.Entries, out var entries, out var error))
                    return Fail(error);
                return Write(() => hosts.Write(entries), "Sync", entries.Count);
            }

            case HelperProtocol.SyncDnsCommand:
            {
                if (!HelperProtocol.TryNormalizeDnsEntries(request.Entries, out var entries, out var error))
                    return Fail(error);
                // Public addresses wait for an administrator's approval; the rest goes in now.
                var pending = entries.Where(e => !IsAllowed(e)).ToList();
                var allowed = entries.Where(IsAllowed).ToList();
                return Write(() => hosts.WriteDns(allowed), "DNS sync", allowed.Count, pending);
            }

            case HelperProtocol.EditLineCommand:
                return EditLine(request);

            default:
                return Fail($"Comando desconhecido: {request.Command}");
        }
    }

    /// <summary>
    /// A line outside Severino's blocks, changed only as asked: the exact line the app read, one
    /// address for the new names, and the same address rules as the DNS block.
    /// </summary>
    private HelperResponse EditLine(HelperRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Line) || request.Line.Contains('\n') || request.Line.Contains('\r'))
            return Fail("Informe a linha a alterar.");
        if (!HelperProtocol.TryNormalizeDnsEntries(request.Entries ?? [], out var entries, out var error))
            return Fail(error);

        string? replacement = null;
        if (entries.Count > 0)
        {
            var addresses = entries.Select(e => e.Address).Distinct(StringComparer.Ordinal).ToList();
            if (addresses.Count != 1)
                return Fail("Uma linha do hosts tem um endereço só.");
            var pending = entries.Where(e => !IsAllowed(e)).ToList();
            if (pending.Count > 0)
                return HelperResponse.Failure("O endereço é público e precisa de aprovação.", HelperVersion, pending);
            // Names in the order they came, as the person typed them.
            var names = new List<string>();
            foreach (var raw in request.Entries!)
            {
                if (DomainName.TryNormalize(raw.Name, out var name, out _, allowSingleLabel: true) && !names.Contains(name))
                    names.Add(name);
            }
            replacement = HostsText.Render(addresses[0], names);
        }

        try
        {
            var result = hosts.Change(text => HostsText.ReplaceLine(text, request.Line, replacement, _now()));
            if (result is null)
                return Fail("A linha mudou no hosts desde que o Severino a leu. Atualize a lista e tente de novo.");
            logger.LogInformation("Line outside Severino's blocks {Action}", replacement is null ? "commented out" : "replaced");
            return HelperResponse.Success(HelperVersion);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Failed to write the hosts file");
            return Fail($"Não foi possível gravar o hosts: {ex.Message}");
        }
    }

    private bool IsAllowed(HostEntry entry) =>
        DnsAddress.TryClassify(entry.Address, out _, out var scope) && (scope != AddressScope.Public || approvals.IsApproved(entry));

    private HelperResponse Write(Func<bool> write, string what, int count, IReadOnlyList<HostEntry>? pending = null)
    {
        try
        {
            var changed = write();
            logger.LogInformation("{What} with {Count} entries ({Result}), {Pending} awaiting approval",
                what, count, changed ? "written" : "unchanged", pending?.Count ?? 0);
            return HelperResponse.Success(HelperVersion, pending);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Failed to write the hosts file");
            return Fail($"Não foi possível gravar o hosts: {ex.Message}");
        }
    }

    private HelperResponse Fail(string error)
    {
        logger.LogWarning("Rejected request: {Error}", error);
        return HelperResponse.Failure(error, HelperVersion);
    }
}
