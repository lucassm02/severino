using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Severino.App.Services;
using Severino.Core.Discovery;

namespace Severino.App.ViewModels;

/// <summary>Settings › WSL: which distros call the service routes by name.</summary>
public sealed partial class SettingsViewModel
{
    private readonly WslCallers _wsl;
    private readonly ServiceDiscovery _discovery;
    private readonly Severino.Core.Dns.DnsSync? _dnsSync;

    /// <summary>Installed distros, chosen or not.</summary>
    public ObservableCollection<WslDistroItem> WslDistros { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoWsl))]
    public partial bool WslLoaded { get; set; }

    public bool HasNoWsl => WslLoaded && WslDistros.Count == 0;

    private void InitializeWsl()
    {
        _wsl.Changed += (_, _) => Dispatch(RefreshWsl);
        _config.Changed += (_, _) => Dispatch(RefreshWsl);
        _ = LoadDistrosAsync();
    }

    private async Task LoadDistrosAsync()
    {
        var installed = await _discovery.InstalledDistrosAsync(CancellationToken.None);
        WslDistros.Clear();
        // A chosen distro that was uninstalled still shows, so it can be turned off.
        foreach (var distro in installed.Union(_config.Current.Settings.WslDistros, StringComparer.OrdinalIgnoreCase))
            WslDistros.Add(new WslDistroItem(distro, SetWslDistro));
        WslLoaded = true;
        OnPropertyChanged(nameof(HasNoWsl));
        RefreshWsl();
    }

    private void SetWslDistro(string distro, bool chosen) =>
        _config.Update(c =>
        {
            var distros = c.Settings.WslDistros.Where(d => !d.Equals(distro, StringComparison.OrdinalIgnoreCase)).ToList();
            if (chosen)
                distros.Add(distro);
            return c with { Settings = c.Settings with { WslDistros = distros } };
        });

    private void RefreshWsl()
    {
        var chosen = _config.Current.Settings.WslDistros;
        var statuses = _wsl.Statuses;
        foreach (var item in WslDistros)
        {
            var isChosen = chosen.Contains(item.Name, StringComparer.OrdinalIgnoreCase);
            var status = statuses.FirstOrDefault(s => s.Distro.Equals(item.Name, StringComparison.OrdinalIgnoreCase));
            item.Refresh(isChosen, isChosen ? status : null);
        }
    }

    [RelayCommand]
    private Task RetryWslAsync() => _wsl.RetryAsync();
}

/// <summary>One distro in Settings › WSL.</summary>
public sealed partial class WslDistroItem(string name, Action<string, bool> setChosen) : ObservableObject
{
    private bool _chosen;

    public string Name { get; } = name;

    public bool IsChosen
    {
        get => _chosen;
        set
        {
            if (value == _chosen)
                return;
            _chosen = value;
            OnPropertyChanged();
            setChosen(Name, value);
        }
    }

    [ObservableProperty]
    public partial string? StatusText { get; set; }

    [ObservableProperty]
    public partial bool IsProblem { get; set; }

    public void Refresh(bool chosen, WslCallerStatus? status)
    {
        if (_chosen != chosen)
        {
            _chosen = chosen;
            OnPropertyChanged(nameof(IsChosen));
        }
        IsProblem = status?.State is WslCallerState.Failed or WslCallerState.Partial;
        StatusText = !chosen ? null : status?.State switch
        {
            null => "Aplicando…",
            WslCallerState.Stopped => "Parada. Recebe os nomes quando iniciar.",
            WslCallerState.Applied when status.Count == 0 => "Ligada. Nenhum serviço a levar ainda.",
            WslCallerState.Applied => status.Count == 1 ? "1 serviço pelo nome." : $"{status.Count} serviços pelo nome.",
            WslCallerState.Partial => $"{status.Count} serviços, com falhas: {status.Detail}",
            _ => status.Detail,
        };
    }
}
