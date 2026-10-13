using Severino.App.ViewModels;
using Wpf.Ui.Controls;

namespace Severino.App.Views;

public partial class ServiceEditorWindow : FluentWindow
{
    public ServiceEditorWindow(ServiceEditorViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        FirstRender.Repaint(this);
        FirstRender.FitScreen(this);
        viewModel.CloseRequested += (_, saved) => DialogResult = saved;
        Loaded += (_, _) => Names.Focus();
    }
}
