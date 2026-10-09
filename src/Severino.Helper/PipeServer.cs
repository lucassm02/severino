using System.IO.Pipes;
using System.Security.Principal;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Severino.Contracts;

namespace Severino.Helper;

/// <summary>
/// Serves one client at a time on a single pipe instance that lives as long as the service,
/// so no other process can create the pipe name in between.
/// </summary>
public sealed class PipeServer(HelperRequestHandler handler, ILogger<PipeServer> logger) : BackgroundService
{
    private static readonly TimeSpan ClientTimeout = TimeSpan.FromSeconds(5);

    public string PipeName { get; init; } = HelperProtocol.PipeName;

    /// <summary>Overrides the SID read from the registry; for tests.</summary>
    public SecurityIdentifier? AllowedUser { get; init; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var user = AllowedUser ?? PipeAccess.ReadAllowedUser();
        if (user is null)
        {
            logger.LogError(@"No valid HKLM\{Key}\{Value}; the pipe will not be opened", PipeAccess.RegistryKey, PipeAccess.AllowedSidValue);
            return;
        }

        using var pipe = NamedPipeServerStreamAcl.Create(
            PipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance,
            inBufferSize: 0,
            outBufferSize: 0,
            PipeAccess.CreateSecurity(user));

        logger.LogInformation("Listening on pipe {Pipe} for {Sid}, version {Version}", PipeName, user, HelperRequestHandler.HelperVersion);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await pipe.WaitForConnectionAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            timeout.CancelAfter(ClientTimeout);
            try
            {
                var message = await MessageFraming.ReadAsync(pipe, HelperProtocol.MaxMessageBytes, timeout.Token);
                var response = handler.Handle(message);
                await MessageFraming.WriteAsync(pipe, HelperProtocol.Serialize(response), timeout.Token);

                // Disconnecting discards anything the client has not read yet, so wait for it
                // to close its end first.
                var sink = new byte[1];
                while (await pipe.ReadAsync(sink, timeout.Token) > 0) { }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException)
            {
                logger.LogWarning("Client dropped: {Reason}", ex.Message);
            }
            finally
            {
                try
                {
                    pipe.Disconnect();
                }
                catch (InvalidOperationException)
                {
                    // The client never got as far as connecting.
                }
            }
        }
    }
}
