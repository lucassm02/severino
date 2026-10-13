using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Severino.App.Resources;
using Severino.Core.Configuration;

namespace Severino.App.ViewModels;

/// <param name="Name">Empty for "none": only the items without an environment count.</param>
public sealed record EnvironmentChoice(string Name, string Label, bool IsActive);

/// <summary>The active environment and the switch to another, for the status bar and the tray.</summary>
public sealed partial class EnvironmentsViewModel : ObservableObject
{
    private readonly ConfigService _config;

    public EnvironmentsViewModel(ConfigService config)
    {
        _config = config;
        Refresh(config.Current);
        config.Changed += (_, c) => Dispatch(() => Refresh(c));
    }

    public ObservableCollection<EnvironmentChoice> Choices { get; } = [];

    /// <summary>Some item has an environment; until then nothing about them shows.</summary>
    [ObservableProperty]
    public partial bool HasAny { get; private set; }

    /// <summary>"Ambiente: staging", or "Nenhum ambiente".</summary>
    [ObservableProperty]
    public partial string Label { get; private set; } = "";

    /// <summary>Everything follows the config: the proxy, the hosts, the DNS block and WSL.</summary>
    [RelayCommand]
    private void Select(string? name) =>
        _config.Update(c => c with { Settings = c.Settings with { ActiveEnvironment = (name ?? "").Trim() } });

    private void Refresh(SeverinoConfig config)
    {
        var names = Environments.List(config);
        var active = config.Settings.ActiveEnvironment;
        HasAny = names.Count > 0;
        Label = active.Length > 0 ? string.Format(Strings.Env_Label, active) : Strings.Env_None;
        Choices.Clear();
        foreach (var name in names)
            Choices.Add(new(name, name, Environments.Same(name, active)));
        if (names.Count > 0)
            Choices.Add(new("", Strings.Env_NoneChoice, active.Length == 0));
    }

    private static void Dispatch(Action action)
    {
        if (Application.Current?.Dispatcher is { } dispatcher)
            dispatcher.BeginInvoke(action);
        else
            action();
    }
}
