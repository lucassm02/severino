using Severino.App.ViewModels;
using Wpf.Ui.Controls;

namespace Severino.App.Views;

public partial class RouteEditorWindow : FluentWindow
{
    public RouteEditorWindow(RouteEditorViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        FirstRender.Repaint(this);
        viewModel.CloseRequested += (_, saved) => DialogResult = saved;
        Loaded += (_, _) => Form.FocusDomain();
    }
}
