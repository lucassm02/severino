using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Severino.App.Services;
using Severino.Core.Configuration;

namespace Severino.App.ViewModels;

public sealed record ThemeOption(AppTheme Value, string Label)
{
    // Screen readers and UI Automation read the item's ToString.
    public override string ToString() => Label;
}

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ConfigService _config;
    private readonly ThemeService _themes;

    public SettingsViewModel(ConfigService config, ThemeService themes)
    {
        _config = config;
        _themes = themes;

        var settings = config.Current.Settings;
        Theme = settings.Theme;
        StartMinimized = settings.StartMinimized;
        HttpPort = settings.HttpPort.ToString();
    }

    public IReadOnlyList<ThemeOption> Themes { get; } =
    [
        new(AppTheme.Auto, "Igual ao Windows"),
        new(AppTheme.Light, "Claro"),
        new(AppTheme.Dark, "Escuro"),
    ];

    public string ConfigDirectory => _config.Directory;

    [ObservableProperty]
    public partial AppTheme Theme { get; set; }

    [ObservableProperty]
    public partial bool StartMinimized { get; set; }

    partial void OnThemeChanged(AppTheme value)
    {
        // Runs once from the constructor too; Update skips the write when nothing changed.
        _config.Update(c => c with { Settings = c.Settings with { Theme = value } });
        _themes.Apply(value);
    }

    partial void OnStartMinimizedChanged(bool value) =>
        _config.Update(c => c with { Settings = c.Settings with { StartMinimized = value } });

    [ObservableProperty]
    public partial string HttpPort { get; set; }

    [ObservableProperty]
    public partial string? HttpPortError { get; set; }

    partial void OnHttpPortChanged(string value) => HttpPortError = null;

    /// <summary>Saves the port; the proxy moves to it right away.</summary>
    [RelayCommand]
    private void ApplyHttpPort()
    {
        if (!int.TryParse(HttpPort?.Trim(), out var port) || port is < 1 or > 65535)
        {
            HttpPortError = "Use um número de 1 a 65535.";
            return;
        }
        HttpPort = port.ToString();
        _config.Update(c => c with { Settings = c.Settings with { HttpPort = port } });
    }

    [RelayCommand]
    private void OpenConfigFolder()
    {
        Directory.CreateDirectory(ConfigDirectory);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{ConfigDirectory}\"") { UseShellExecute = true });
    }
}
