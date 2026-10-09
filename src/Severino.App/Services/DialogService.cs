using System.Windows;
using Severino.App.ViewModels;
using Severino.App.Views;
using Severino.Core.Configuration;
using Severino.Core.Domains;
using Severino.Core.Routes;

namespace Severino.App.Services;

public sealed class DialogService(RouteService routes, DomainInspector inspector)
{
    /// <summary>Opens the route form; returns the saved route, or null when cancelled.</summary>
    public RouteEntry? EditRoute(RouteEntry? existing, bool isCopy = false)
    {
        var viewModel = new RouteEditorViewModel(routes, inspector, existing, isCopy);
        var window = new RouteEditorWindow(viewModel) { Owner = Application.Current.MainWindow };
        return window.ShowDialog() == true ? viewModel.Saved : null;
    }

    public static void ShowMessage(string title, string message) =>
        MessageBox.Show(Application.Current.MainWindow, message, title, MessageBoxButton.OK, MessageBoxImage.Information);
}
