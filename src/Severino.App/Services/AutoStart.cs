using System.IO;
using Microsoft.Win32;

namespace Severino.App.Services;

/// <summary>
/// "Iniciar com o Windows": a value under the current user's Run key, so no admin is needed.
/// The entry passes <see cref="Argument"/>, and the app then opens straight to the tray.
/// </summary>
public sealed class AutoStart(string keyPath = AutoStart.RunKey, string? executable = null)
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "Severino";
    public const string Argument = "--autostart";

    private readonly string _executable = executable ?? Environment.ProcessPath
        ?? throw new InvalidOperationException("The executable path is unknown.");

    /// <summary>What the entry should say for this executable.</summary>
    public string Command => $"\"{_executable}\" {Argument}";

    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath);
            return key?.GetValue(ValueName) is string;
        }
    }

    public void Enable()
    {
        using var key = Registry.CurrentUser.CreateSubKey(keyPath);
        key.SetValue(ValueName, Command, RegistryValueKind.String);
    }

    public void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>
    /// Points an entry whose executable is gone (the app moved) at this one. Never creates an
    /// entry, and leaves one that works alone: a development build must not take over the
    /// installed app's.
    /// </summary>
    public void Repair()
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
        if (key?.GetValue(ValueName) is string current && current != Command && !File.Exists(ExecutableOf(current)))
            key.SetValue(ValueName, Command, RegistryValueKind.String);
    }

    private static string ExecutableOf(string command) =>
        command.StartsWith('"') && command.IndexOf('"', 1) is > 0 and var end
            ? command[1..end]
            : command.Split(' ', 2)[0];
}
