namespace Severino.Core.Configuration;

/// <summary>Holds the config in use and persists every change.</summary>
public sealed class ConfigService(ConfigStore store)
{
    private readonly Lock _gate = new();
    private SeverinoConfig _current = new();

    public event EventHandler<SeverinoConfig>? Changed;

    public SeverinoConfig Current
    {
        get { lock (_gate) return _current; }
    }

    public string Directory => store.Directory;

    public ConfigLoadResult Load()
    {
        var result = store.Load();
        lock (_gate) _current = result.Config;
        return result;
    }

    public SeverinoConfig Update(Func<SeverinoConfig, SeverinoConfig> change)
    {
        SeverinoConfig updated;
        lock (_gate)
        {
            updated = change(_current);
            if (updated == _current)
                return updated;
            store.Save(updated);
            _current = updated;
        }
        Changed?.Invoke(this, updated);
        return updated;
    }
}
