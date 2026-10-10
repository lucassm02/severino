using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Severino.Core.Network;

/// <summary>A port a local dev server could be on, for the route form's port list.</summary>
/// <param name="Tool">The dev tool recognised from the command line, like "vite"; null when unknown or unreadable.</param>
public sealed record ListeningPort(int Port, int ProcessId, string ProcessName, string? Tool)
{
    /// <summary>"5173 · node (vite)".</summary>
    public string Label => Tool is null ? $"{Port} · {ProcessName}" : $"{Port} · {ProcessName} ({Tool})";

    public string PortText => Port.ToString();

    // An editable ComboBox shows this in its text box.
    public override string ToString() => PortText;
}

/// <summary>
/// The TCP ports listening on loopback or on every address, minus what is never a dev server:
/// Windows' own services, the system ports below 1024 (80 and 443 stay), the dynamic range
/// (unless a dev runtime holds it) and, in the app, Severino itself.
/// </summary>
public static partial class ListeningPorts
{
    private static readonly HashSet<string> WindowsServices = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "svchost", "lsass", "wininit", "services", "spoolsv",
    };

    /// <summary>Above 49151 is Windows' dynamic range, mostly apps talking to themselves.</summary>
    private const int DynamicPorts = 49152;

    /// <summary>Processes that run dev servers, kept even in the dynamic range.</summary>
    private static readonly HashSet<string> DevRuntimes = new(StringComparer.OrdinalIgnoreCase)
    {
        "node", "bun", "deno", "dotnet", "python", "java", "ruby", "php", "go", "WSL", "Docker",
    };

    /// <summary>Processes that relay someone else's server: the name says where it really runs.</summary>
    private static readonly Dictionary<string, string> Relays = new(StringComparer.OrdinalIgnoreCase)
    {
        ["wslrelay"] = "WSL",
        ["com.docker.backend"] = "Docker",
        ["vpnkit"] = "Docker",
    };

    /// <summary>First match wins, so frameworks built on Vite or webpack come before them.</summary>
    private static readonly (string Tool, string[] Markers)[] Tools =
    [
        ("storybook", ["storybook"]),
        ("nuxt", ["nuxt", "nuxi"]),
        ("astro", ["astro"]),
        ("angular", ["@angular"]),
        ("next", ["next"]),
        ("vite", ["vite"]),
        ("webpack", ["webpack", "webpack-dev-server", "react-scripts"]),
        ("dotnet watch", ["dotnet-watch"]),
    ];

    /// <param name="excludeProcessId">The app passes its own id, so its proxy ports stay out of the list.</param>
    public static IReadOnlyList<ListeningPort> List(int? excludeProcessId = null)
    {
        var names = new Dictionary<int, (string Name, string? Tool)>();
        return Filter(PortOwner.Listeners(), excludeProcessId, pid =>
        {
            if (!names.TryGetValue(pid, out var info))
                names[pid] = info = Describe(pid);
            return info;
        });
    }

    /// <summary>The filtering and ordering, apart from the system calls so tests can feed it rows.</summary>
    public static IReadOnlyList<ListeningPort> Filter(IEnumerable<TcpListenerRow> rows, int? excludeProcessId, Func<int, (string Name, string? Tool)> describe) =>
        [.. rows
            .Where(r => (IPAddress.IsLoopback(r.Address) || r.Address.Equals(IPAddress.Any) || r.Address.Equals(IPAddress.IPv6Any))
                && r.ProcessId is not (0 or 4)
                && r.ProcessId != excludeProcessId
                && (r.Port >= 1024 || r.Port is 80 or 443))
            .DistinctBy(r => r.Port)
            .Select(r => (Row: r, Info: describe(r.ProcessId)))
            .Where(x => !WindowsServices.Contains(x.Info.Name)
                && (x.Row.Port < DynamicPorts || x.Info.Tool is not null || DevRuntimes.Contains(x.Info.Name)))
            .Select(x => new ListeningPort(x.Row.Port, x.Row.ProcessId, x.Info.Name, x.Info.Tool))
            .OrderBy(p => p.Port)];

    /// <summary>The dev tool a command line runs, by the names in its paths: "...\node_modules\vite\bin\vite.js" is vite.</summary>
    public static string? ToolFrom(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            return null;
        var stems = Separators().Split(commandLine.ToLowerInvariant())
            .Select(token => Extension().Replace(token, ""))
            .ToHashSet();
        return Tools.FirstOrDefault(t => t.Markers.Any(stems.Contains)).Tool;
    }

    private static (string Name, string? Tool) Describe(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return Relays.TryGetValue(process.ProcessName, out var relay)
                ? (relay, null)
                : (process.ProcessName, ToolFrom(CommandLine(pid)));
        }
        catch (ArgumentException)
        {
            return ("processo encerrado", null);
        }
    }

    [GeneratedRegex(@"[\\/\s""']+")]
    private static partial Regex Separators();

    [GeneratedRegex(@"\.(js|mjs|cjs|cmd|ps1|dll|exe)$")]
    private static partial Regex Extension();

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ProcessCommandLineInformation = 60;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);

    /// <summary>
    /// Another process's command line, via <c>NtQueryInformationProcess</c>, which hands back a copy
    /// without reading the process's memory. Null when access is denied, as for elevated processes.
    /// </summary>
    public static string? CommandLine(int pid)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == 0)
            return null;
        try
        {
            var status = NtQueryInformationProcess(handle, ProcessCommandLineInformation, 0, 0, out var length);
            if (status != StatusInfoLengthMismatch || length <= 0)
                return null;
            var buffer = Marshal.AllocHGlobal(length);
            try
            {
                if (NtQueryInformationProcess(handle, ProcessCommandLineInformation, buffer, length, out _) != 0)
                    return null;
                // UNICODE_STRING { USHORT Length; USHORT MaximumLength; PWSTR Buffer; }, the text right after it.
                var bytes = (ushort)Marshal.ReadInt16(buffer);
                var text = Marshal.ReadIntPtr(buffer, IntPtr.Size);
                return Marshal.PtrToStringUni(text, bytes / 2);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryInformationProcess(nint handle, int infoClass, nint buffer, int length, out int returnLength);
}
