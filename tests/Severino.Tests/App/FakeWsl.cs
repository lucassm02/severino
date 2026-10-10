using Severino.Core.Discovery;
using Severino.Core.Wsl;

namespace Severino.Tests.App;

/// <summary>Records the scripts sent to each distro and answers with a fixed result.</summary>
public sealed class FakeWslShell : IWslShell
{
    public List<(string Distro, string Script)> Runs { get; } = [];

    public CommandResult Answer { get; set; } = new(0, "", "", false);

    public Task<CommandResult> RunAsRootAsync(string distro, string script, CancellationToken cancellationToken)
    {
        lock (Runs)
            Runs.Add((distro, script));
        return Task.FromResult(Answer);
    }
}

/// <summary>wsl.exe --list answering with <see cref="Running"/>; every other command is "not installed".</summary>
public sealed class FakeWslRunner : ICommandRunner
{
    public List<string> Running { get; } = [];

    public List<string> Installed { get; } = [];

    public Task<CommandResult> RunAsync(CommandSource source, string program, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var args = string.Join(' ', arguments);
        return Task.FromResult((source.IsWsl, program, args) switch
        {
            (false, "wsl.exe", "--list --running --quiet") => new CommandResult(0, string.Join('\n', Running), "", false),
            (false, "wsl.exe", "--list --quiet") => new CommandResult(0, string.Join('\n', Installed.Union(Running)), "", false),
            _ => new CommandResult(-1, "", "not installed", false),
        });
    }
}
