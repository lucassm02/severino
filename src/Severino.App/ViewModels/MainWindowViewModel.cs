namespace Severino.App.ViewModels;

public sealed class MainWindowViewModel(SettingsViewModel settings)
{
    public SettingsViewModel Settings { get; } = settings;
}
