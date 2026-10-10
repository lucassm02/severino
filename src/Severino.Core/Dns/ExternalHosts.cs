using System.Text;
using Severino.Contracts;

namespace Severino.Core.Dns;

/// <summary>
/// The hosts lines that are not Severino's, read from the file and read again when it changes.
/// Anyone can read the hosts file; only the Helper writes it.
/// </summary>
public sealed class ExternalHosts : IDisposable
{
    private readonly string _path;
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;

    public ExternalHosts(string? path = null)
    {
        _path = path ?? Path.Combine(Environment.SystemDirectory, "drivers", "etc", "hosts");
        Lines = Read();
    }

    public IReadOnlyList<HostsLine> Lines { get; private set; }

    /// <summary>Raised on a thread-pool thread when the lines changed.</summary>
    public event EventHandler? Changed;

    /// <summary>The names of the lines in effect (not the ones Severino commented out).</summary>
    public IReadOnlySet<string> Names =>
        Lines.Where(l => !l.Removed).SelectMany(l => l.Names).Select(n => n.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);

    public void Start()
    {
        if (_watcher is not null || !Directory.Exists(Path.GetDirectoryName(_path)))
            return;
        _debounce = new Timer(_ => Refresh());
        _watcher = new FileSystemWatcher(Path.GetDirectoryName(_path)!, Path.GetFileName(_path))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        // The Helper writes by renaming a temp file over the hosts: that is a rename, not a change.
        _watcher.Changed += (_, _) => _debounce.Change(200, Timeout.Infinite);
        _watcher.Renamed += (_, _) => _debounce.Change(200, Timeout.Infinite);
        _watcher.Created += (_, _) => _debounce.Change(200, Timeout.Infinite);
    }

    /// <summary>Reads the file again now, e.g. after the Helper refused a change because the line moved.</summary>
    public void Refresh()
    {
        var lines = Read();
        if (lines.Select(l => l.Text + l.Note).SequenceEqual(Lines.Select(l => l.Text + l.Note)))
            return;
        Lines = lines;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounce?.Dispose();
    }

    private IReadOnlyList<HostsLine> Read()
    {
        // Latin-1, as the Helper reads it: the text of each line must match byte for byte.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return File.Exists(_path) ? HostsText.ExternalLines(Encoding.Latin1.GetString(File.ReadAllBytes(_path))) : [];
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(50); // the Helper or an antivirus has it open for a moment
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Lines ?? [];
            }
        }
    }
}
