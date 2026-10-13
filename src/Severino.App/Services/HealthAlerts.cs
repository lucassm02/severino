namespace Severino.App.Services;

/// <summary>
/// When a health change is worth a notice: a target that answered and then stayed down for
/// <paramref name="grace"/> (a dev server restarting takes a few seconds, and says nothing), and
/// its return after that. A target never seen answering since the app opened is not news.
/// </summary>
public sealed class HealthAlerts(TimeSpan grace)
{
    private readonly Lock _gate = new();
    private readonly HashSet<Guid> _seenUp = [];
    private readonly Dictionary<Guid, DateTimeOffset> _downSince = [];
    private readonly HashSet<Guid> _told = [];
    private readonly HashSet<Guid> _back = [];

    public void Observe(Guid id, bool up, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (up)
            {
                _seenUp.Add(id);
                _downSince.Remove(id);
                if (_told.Remove(id))
                    _back.Add(id);
            }
            else if (_seenUp.Contains(id))
            {
                _downSince.TryAdd(id, now);
            }
        }
    }

    /// <summary>
    /// What to tell now, each once: down past the grace, and back after being told down. Only ids
    /// <paramref name="tell"/> accepts; the others are dropped, so turning a notice on later does
    /// not bring up an old fall.
    /// </summary>
    public (IReadOnlyList<Guid> Down, IReadOnlyList<Guid> Back) Take(DateTimeOffset now, Func<Guid, bool> tell)
    {
        lock (_gate)
        {
            var down = new List<Guid>();
            foreach (var (id, since) in _downSince.Where(d => now - d.Value >= grace && !_told.Contains(d.Key)).ToList())
            {
                if (tell(id))
                {
                    _told.Add(id);
                    down.Add(id);
                }
                else
                {
                    _downSince.Remove(id);
                }
            }
            var back = _back.Where(tell).ToList();
            _back.Clear();
            return (down, back);
        }
    }

    /// <summary>Paused or stopped: what is down now means nothing; start over from what answers next.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _seenUp.Clear();
            _downSince.Clear();
            _told.Clear();
            _back.Clear();
        }
    }
}
