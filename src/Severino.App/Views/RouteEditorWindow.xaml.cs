using Severino.App.ViewModels;
using Wpf.Ui.Controls;

namespace Severino.App.Views;

public partial class RouteEditorWindow : FluentWindow
{
    public RouteEditorWindow(RouteEditorViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        viewModel.CloseRequested += (_, saved) => DialogResult = saved;
        Loaded += (_, _) => DomainBox.Focus();
    }
}
