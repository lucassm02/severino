using System.Diagnostics;
using System.Text;
using Severino.Core.Discovery;

namespace Severino.Core.Wsl;

public interface IWslShell
{
    /// <summary>Runs <paramref name="script"/> with sh as root in <paramref name="distro"/>. Starts the distro if it is stopped.</summary>
    Task<CommandResult> RunAsRootAsync(string distro, string script, CancellationToken cancellationToken);
}

/// <summary>
/// <c>wsl.exe -d distro -u root</c> with the script on stdin, so its size and quoting never
/// meet the Windows command line. Root in the distro needs no password and no elevation on Windows.
/// </summary>
public sealed class WslShell(TimeSpan? timeout = null) : IWslShell
{
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(20);

    public async Task<CommandResult> RunAsRootAsync(string distro, string script, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("wsl.exe")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        start.Environment["WSL_UTF8"] = "1";
        foreach (var argument in new[] { "-d", distro, "-u", "root", "--exec", "sh", "-s" })
            start.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return new(-1, "", ex.Message, TimedOut: false);
        }

        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.StandardInput.WriteAsync(script.Replace("\r\n", "\n").AsMemory(), cancellationToken);
        process.StandardInput.Close();

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(_timeout);
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            cancellationToken.ThrowIfCancellationRequested();
            return new(-1, "", "", TimedOut: true);
        }
        return new(process.ExitCode, await output, await error, TimedOut: false);
    }
}
