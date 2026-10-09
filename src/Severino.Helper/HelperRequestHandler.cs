using System.Text.Json;
using Microsoft.Extensions.Logging;
using Severino.Contracts;

namespace Severino.Helper;

/// <summary>Turns one pipe message into one response. Trusts nothing in the message.</summary>
public sealed class HelperRequestHandler(IHostsWriter hosts, ILogger<HelperRequestHandler> logger)
{
    public static string HelperVersion { get; } =
        typeof(HelperRequestHandler).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

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
                if (!HelperProtocol.TryNormalizeDomains(request.Domains, out var domains, out var error))
                    return Fail(error);
                try
                {
                    var changed = hosts.Write(domains);
                    logger.LogInformation("Sync with {Count} domains ({Result})", domains.Count, changed ? "written" : "unchanged");
                    return HelperResponse.Success(HelperVersion);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogError(ex, "Failed to write the hosts file");
                    return Fail($"Não foi possível gravar o hosts: {ex.Message}");
                }

            default:
                return Fail($"Comando desconhecido: {request.Command}");
        }
    }

    private HelperResponse Fail(string error)
    {
        logger.LogWarning("Rejected request: {Error}", error);
        return HelperResponse.Failure(error, HelperVersion);
    }
}
