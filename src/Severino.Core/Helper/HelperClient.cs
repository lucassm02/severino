using System.IO.Pipes;
using System.Text.Json;
using Severino.Contracts;

namespace Severino.Core.Helper;

public interface IHelperClient
{
    /// <exception cref="HelperUnavailableException">The Helper is not running or did not answer.</exception>
    Task<HelperResponse> SendAsync(HelperRequest request, CancellationToken cancellationToken);
}

public sealed class HelperUnavailableException(string message, Exception inner) : Exception(message, inner);

/// <summary>Talks to Severino.Helper over its named pipe, one connection per request.</summary>
public sealed class HelperClient(string pipeName = HelperProtocol.PipeName) : IHelperClient
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(5);

    public async Task<HelperResponse> SendAsync(HelperRequest request, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ResponseTimeout);
        try
        {
            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(ConnectTimeout, timeout.Token);
            await MessageFraming.WriteAsync(pipe, HelperProtocol.Serialize(request), timeout.Token);
            var reply = await MessageFraming.ReadAsync(pipe, HelperProtocol.MaxMessageBytes, timeout.Token);
            return HelperProtocol.DeserializeResponse(reply);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HelperUnavailableException("O serviço auxiliar não respondeu a tempo.", ex);
        }
        catch (TimeoutException ex)
        {
            throw new HelperUnavailableException("O serviço auxiliar não está rodando.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new HelperUnavailableException("O serviço auxiliar recusou a conexão deste usuário.", ex);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
        {
            throw new HelperUnavailableException("A conversa com o serviço auxiliar falhou.", ex);
        }
    }
}
