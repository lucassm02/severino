using CommunityToolkit.Mvvm.ComponentModel;
using Severino.App.Services;

namespace Severino.App.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    public MainWindowViewModel(RoutesViewModel routes, ServicesViewModel services, DnsViewModel dns, RequestsViewModel requests, SettingsViewModel settings,
        StatusBarViewModel status, Navigation navigation)
    {
        Routes = routes;
        Services = services;
        Dns = dns;
        Requests = requests;
        Settings = settings;
        Status = status;
        navigation.Requested += (_, request) => Show(request);
    }

    public RoutesViewModel Routes { get; }
    public ServicesViewModel Services { get; }
    public DnsViewModel Dns { get; }
    public RequestsViewModel Requests { get; }
    public SettingsViewModel Settings { get; }
    public StatusBarViewModel Status { get; }

    [ObservableProperty]
    public partial int SelectedTab { get; set; }

    private void Show(NavigationRequest request)
    {
        if (request.Search is { } search)
        {
            ListPageViewModel? page = request.Tab switch
            {
                AppTab.Routes => Routes,
                AppTab.Services => Services,
                AppTab.Dns => Dns,
                _ => null,
            };
            page?.Search = search;
        }
        SelectedTab = (int)request.Tab;
    }
}
