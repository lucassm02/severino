using System.Windows;
using Microsoft.Win32;
using Severino.App.ViewModels;
using Severino.App.Views;
using Severino.Core.Configuration;
using Severino.Core.Domains;
using Severino.Core.Routes;

namespace Severino.App.Services;

public sealed class DialogService(RouteService routes, DomainInspector inspector, HttpsService https, Navigation navigation)
{
    /// <summary>Opens the route form; returns the saved route, or null when cancelled.</summary>
    public RouteEntry? EditRoute(RouteEntry? existing, bool isCopy = false)
    {
        var viewModel = new RouteEditorViewModel(routes, inspector, https, navigation, existing, isCopy);
        var window = new RouteEditorWindow(viewModel) { Owner = Application.Current.MainWindow };
        return window.ShowDialog() == true ? viewModel.Saved : null;
    }

    public static void ShowMessage(string title, string message) =>
        MessageBox.Show(Application.Current.MainWindow, message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public static bool Confirm(string title, string message) =>
        MessageBox.Show(Application.Current.MainWindow, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    /// <summary>Asks where to save a file; null when cancelled.</summary>
    public static string? PickSavePath(string fileName, string filter)
    {
        var dialog = new SaveFileDialog
        {
            FileName = fileName,
            Filter = filter,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FileName : null;
    }
}
