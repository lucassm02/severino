using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Severino.App.Resources;

namespace Severino.App.ViewModels;

/// <summary>Settings › Terminal: the PowerShell module that talks to the open app.</summary>
public sealed partial class SettingsViewModel
{
    /// <summary>Where the installer puts the module, so any PowerShell finds it.</summary>
    private static readonly string ModulePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsPowerShell", "Modules", "Severino", "Severino.psd1");

    public string TerminalExamples =>
        "New-SeverinoRoute api.sev http://localhost:8080 -Https -Group meuapp\n" +
        "Set-SeverinoDns sql.interno 10.0.0.8\n" +
        "Disable-SeverinoRoute -Group meuapp";

    public string TerminalStatus => File.Exists(ModulePath)
        ? Strings.Terminal_Installed
        : Strings.Terminal_NotInstalled;

    [ObservableProperty]
    public partial string? TerminalCopied { get; set; }

    [RelayCommand]
    private async Task CopyTerminalExamplesAsync()
    {
        Clipboard.SetText(TerminalExamples);
        TerminalCopied = Strings.Common_Copied;
        await Task.Delay(TimeSpan.FromSeconds(2));
        TerminalCopied = null;
    }
}
