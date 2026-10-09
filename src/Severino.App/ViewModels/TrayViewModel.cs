using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Severino.App.Services;

namespace Severino.App.ViewModels;

public sealed partial class TrayViewModel(ShellService shell, ILogger<TrayViewModel> logger)
{
    [RelayCommand]
    private void ShowWindow() => shell.ShowMainWindow();

    [RelayCommand]
    private Task Exit()
    {
        logger.LogInformation("Exit requested from the tray");
        return shell.ExitAsync();
    }
}
