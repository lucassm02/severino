using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Severino.App.Views;

public partial class RoutesView : UserControl
{
    public RoutesView() => InitializeComponent();

    /// <summary>The ⋯ button opens its menu on a left click too.</summary>
    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { ContextMenu: { } menu } button)
            return;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.DataContext = button.DataContext;
        menu.IsOpen = true;
    }
}
