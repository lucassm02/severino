using Severino.Core.Configuration;

namespace Severino.Tests.Configuration;

public sealed class ConfigServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "severino-tests", Guid.NewGuid().ToString("N"));
    private readonly ConfigService _service;

    public ConfigServiceTests() => _service = new ConfigService(new ConfigStore(_dir));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Update_persists_and_raises_changed()
    {
        _service.Load();
        SeverinoConfig? raised = null;
        _service.Changed += (_, c) => raised = c;

        var updated = _service.Update(c => c with { Settings = c.Settings with { Theme = AppTheme.Light } });

        Assert.Same(updated, raised);
        Assert.Same(updated, _service.Current);
        Assert.Equal(AppTheme.Light, new ConfigStore(_dir).Load().Config.Settings.Theme);
    }

    [Fact]
    public void Update_without_change_does_not_write()
    {
        _service.Load();
        var raised = false;
        _service.Changed += (_, _) => raised = true;

        _service.Update(c => c);

        Assert.False(raised);
        Assert.False(Directory.Exists(_dir));
    }
}
