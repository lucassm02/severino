using CommunityToolkit.Mvvm.Input;
using Severino.App.Services;

namespace Severino.App.ViewModels;

public sealed partial class TrayViewModel(ShellService shell)
{
    [RelayCommand]
    private void ShowWindow() => shell.ShowMainWindow();

    [RelayCommand]
    private Task Exit() => shell.ExitAsync();
}
