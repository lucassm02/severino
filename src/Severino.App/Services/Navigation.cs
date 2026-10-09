namespace Severino.App.Services;

/// <summary>Lets forms and the status bar send the user to a tab of the main window.</summary>
public sealed class Navigation
{
    public event EventHandler? SettingsRequested;

    public void ShowSettings() => SettingsRequested?.Invoke(this, EventArgs.Empty);
}
