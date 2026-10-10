namespace Severino.App.Services;

/// <summary>The tabs of the main window, in their order.</summary>
public enum AppTab
{
    Routes,
    Services,
    Dns,
    Requests,
    Settings,
}

/// <param name="Search">Typed into the tab's search box, so it opens filtered; null leaves it as is.</param>
public sealed record NavigationRequest(AppTab Tab, string? Search = null);

/// <summary>Lets forms, rows and the status bar send the user to a tab of the main window.</summary>
public sealed class Navigation
{
    public event EventHandler<NavigationRequest>? Requested;

    public void Show(AppTab tab, string? search = null) => Requested?.Invoke(this, new NavigationRequest(tab, search));

    public void ShowSettings() => Show(AppTab.Settings);
}
