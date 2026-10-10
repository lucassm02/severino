using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.Logging;
using Severino.Contracts;
using Severino.Core.Control;

namespace Severino.App.Services;

/// <summary>
/// The pipe the PowerShell module talks to: <c>Severino.Control.&lt;SID&gt;</c>, open only to this
/// Windows account and never over the network. One request per connection, as with the Helper.
/// </summary>
public sealed class ControlServer(ControlHandler handler, ILogger<ControlServer> logger, string? pipeName = null) : IAsyncDisposable
{
    private static readonly TimeSpan ClientTimeout = TimeSpan.FromSeconds(10);
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    /// <summary>The pipe name for the current user; the module builds the same one.</summary>
    public static string PipeName => $"Severino.Control.{WindowsIdentity.GetCurrent().User!.Value}";

    public void Start() => _loop ??= Task.Run(() => ServeAsync(_stop.Token));

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_loop is not null)
            await _loop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        _stop.Dispose();
    }

    private async Task ServeAsync(CancellationToken stop)
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));

        NamedPipeServerStream pipe;
        try
        {
            // FirstPipeInstance: if something else already holds the name, we do not serve under it.
            pipe = NamedPipeServerStreamAcl.Create(pipeName ?? PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 0, 0, security);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Control pipe not opened");
            return;
        }

        using (pipe)
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    await pipe.WaitForConnectionAsync(stop);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
                timeout.CancelAfter(ClientTimeout);
                try
                {
                    var message = await MessageFraming.ReadAsync(pipe, ControlHandler.MaxMessageBytes, timeout.Token);
                    var response = handler.Handle(Encoding.UTF8.GetString(message));
                    await MessageFraming.WriteAsync(pipe, Encoding.UTF8.GetBytes(response), timeout.Token);
                    var sink = new byte[1];
                    while (await pipe.ReadAsync(sink, timeout.Token) > 0) { }
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException)
                {
                    logger.LogDebug("Control client dropped: {Reason}", ex.Message);
                }
                finally
                {
                    try
                    {
                        pipe.Disconnect();
                    }
                    catch (InvalidOperationException)
                    {
                    }
                }
            }
        }
    }
}
