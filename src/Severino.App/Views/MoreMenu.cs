using System.Windows;
using System.Windows.Controls.Primitives;

namespace Severino.App.Views;

public static class MoreMenu
{
    /// <summary>The ⋯ button opens its menu on a left click too.</summary>
    public static void Open(object sender)
    {
        if (sender is not FrameworkElement { ContextMenu: { } menu } button)
            return;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.DataContext = button.DataContext;
        menu.IsOpen = true;
    }
}
