using Severino.App.ViewModels;
using Wpf.Ui.Controls;

namespace Severino.App.Views;

public partial class ServiceEditorWindow : FluentWindow
{
    public ServiceEditorWindow(ServiceEditorViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        viewModel.CloseRequested += (_, saved) => DialogResult = saved;
        Loaded += (_, _) => Names.Focus();
    }
}
