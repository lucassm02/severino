using CommunityToolkit.Mvvm.ComponentModel;
using Severino.App.Services;

namespace Severino.App.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    public const int SettingsTab = 2;

    public MainWindowViewModel(RoutesViewModel routes, SettingsViewModel settings, StatusBarViewModel status, Navigation navigation)
    {
        Routes = routes;
        Settings = settings;
        Status = status;
        navigation.SettingsRequested += (_, _) => SelectedTab = SettingsTab;
    }

    public RoutesViewModel Routes { get; }
    public SettingsViewModel Settings { get; }
    public StatusBarViewModel Status { get; }

    [ObservableProperty]
    public partial int SelectedTab { get; set; }
}
