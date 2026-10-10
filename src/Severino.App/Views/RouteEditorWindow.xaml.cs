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

    // The list goes stale quickly while you start servers; read it again each time it opens.
    private void OnPortsOpening(object? sender, EventArgs e) => _ = ((RouteEditorViewModel)DataContext).RefreshPortsAsync();
}
