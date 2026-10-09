using CommunityToolkit.Mvvm.ComponentModel;

namespace Severino.App.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    public const int SettingsTab = 2;

    public MainWindowViewModel(RoutesViewModel routes, SettingsViewModel settings, StatusBarViewModel status)
    {
        Routes = routes;
        Settings = settings;
        Status = status;
        status.OpenSettingsRequested += (_, _) => SelectedTab = SettingsTab;
    }

    public RoutesViewModel Routes { get; }
    public SettingsViewModel Settings { get; }
    public StatusBarViewModel Status { get; }

    [ObservableProperty]
    public partial int SelectedTab { get; set; }
}
