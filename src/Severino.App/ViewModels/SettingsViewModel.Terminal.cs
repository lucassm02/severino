using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Severino.App.ViewModels;

/// <summary>Settings › Terminal: the PowerShell module that talks to the open app.</summary>
public sealed partial class SettingsViewModel
{
    /// <summary>Where the installer puts the module, so any PowerShell finds it.</summary>
    private static readonly string ModulePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsPowerShell", "Modules", "Severino", "Severino.psd1");

    public string TerminalExamples =>
        "New-SeverinoRoute api.sev http://localhost:8080 -Https -Group callfred\n" +
        "Set-SeverinoDns sql.interno 10.0.0.8\n" +
        "Disable-SeverinoRoute -Group callfred";

    public string TerminalStatus => File.Exists(ModulePath)
        ? "Instalado. Com o app aberto, qualquer PowerShell usa os comandos, com as mesmas regras das telas. Get-Command -Module Severino lista todos."
        : "O instalador põe o módulo no PowerShell. Rodando do código-fonte, importe antes: Import-Module ./powershell/Severino.";

    [ObservableProperty]
    public partial string? TerminalCopied { get; set; }

    [RelayCommand]
    private async Task CopyTerminalExamplesAsync()
    {
        Clipboard.SetText(TerminalExamples);
        TerminalCopied = "Copiado";
        await Task.Delay(TimeSpan.FromSeconds(2));
        TerminalCopied = null;
    }
}
