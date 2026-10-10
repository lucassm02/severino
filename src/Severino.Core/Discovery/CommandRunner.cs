using System.Diagnostics;
using System.Text;

namespace Severino.Core.Discovery;

/// <summary>Where a tool runs: Windows itself, or a WSL distro.</summary>
public sealed record CommandSource(string? Distro)
{
    public static readonly CommandSource Windows = new((string?)null);

    public static CommandSource Wsl(string distro) => new(distro);

    public bool IsWsl => Distro is not null;

    /// <summary>"windows" or "wsl:Ubuntu-22.04", as service routes record their origin.</summary>
    public string Id => Distro is null ? "windows" : $"wsl:{Distro}";

    public static CommandSource FromId(string id) =>
        id.StartsWith("wsl:", StringComparison.Ordinal) ? Wsl(id[4..]) : Windows;

    public override string ToString() => Distro is null ? "Windows" : $"WSL · {Distro}";
}

public sealed record CommandResult(int ExitCode, string Output, string Error, bool TimedOut)
{
    public bool Succeeded => ExitCode == 0 && !TimedOut;
}

public interface ICommandRunner
{
    /// <summary>Runs <paramref name="program"/> with <paramref name="arguments"/> where <paramref name="source"/> says.</summary>
    Task<CommandResult> RunAsync(CommandSource source, string program, IReadOnlyList<string> arguments, CancellationToken cancellationToken);
}

/// <summary>
/// Runs tools as processes. In WSL it goes through a login shell, since tools installed per
/// user (docker in some setups, kubectl plugins) are only on a login PATH. Every run has a hard
/// limit: an unreachable cluster kept kubectl retrying for 82 s.
/// </summary>
public sealed class CommandRunner(TimeSpan? timeout = null) : ICommandRunner
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private readonly TimeSpan _timeout = timeout ?? DefaultTimeout;

    public async Task<CommandResult> RunAsync(CommandSource source, string program, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        // Without it, wsl.exe's own output (the distro list, its errors) comes out in UTF-16.
        start.Environment["WSL_UTF8"] = "1";
        if (source.Distro is { } distro)
        {
            start.FileName = "wsl.exe";
            foreach (var argument in WslArguments(distro, program, arguments, _timeout))
                start.ArgumentList.Add(argument);
        }
        else
        {
            start.FileName = program;
            foreach (var argument in arguments)
                start.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return new(-1, "", ex.Message, TimedOut: false); // not installed
        }

        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
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

    /// <summary>
    /// wsl.exe arguments for a login shell running one command. Each argument is single-quoted
    /// for bash, so nothing in it is interpreted. Linux's own <c>timeout</c> wraps it: killing
    /// wsl.exe on this side would leave the command running in the distro.
    /// </summary>
    public static IReadOnlyList<string> WslArguments(string distro, string program, IReadOnlyList<string> arguments, TimeSpan limit) =>
        ["-d", distro, "--exec", "bash", "-lc",
            $"timeout {Math.Max(1, (int)limit.TotalSeconds)} " + string.Join(' ', new[] { program }.Concat(arguments).Select(ShellQuote))];

    public static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";
}
