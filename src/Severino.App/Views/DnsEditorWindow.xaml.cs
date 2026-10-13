using Severino.App.ViewModels;
using Wpf.Ui.Controls;

namespace Severino.App.Views;

public partial class DnsEditorWindow : FluentWindow
{
    public DnsEditorWindow(DnsEditorViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        FirstRender.Repaint(this);
        viewModel.CloseRequested += (_, saved) => DialogResult = saved;
        Loaded += (_, _) => Names.Focus();
    }
}
