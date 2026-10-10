using System.Windows.Controls;
using Severino.App.ViewModels;

namespace Severino.App.Views;

public partial class RouteForm : UserControl
{
    public RouteForm() => InitializeComponent();

    public void FocusDomain() => DomainBox.Focus();

    // The list goes stale quickly while you start servers; read it again each time it opens.
    private void OnPortsOpening(object? sender, EventArgs e) => _ = ((RouteEditorViewModel)DataContext).RefreshPortsAsync();
}
