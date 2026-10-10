using System.Windows;
using System.Windows.Controls;
using Severino.App.ViewModels;

namespace Severino.App.Tray;

/// <summary>
/// Styles only the route entries of the tray menu. A plain ItemContainerStyle for MenuItem would
/// also hit the separators in the same menu and throw.
/// </summary>
public sealed class TrayMenuStyleSelector : StyleSelector
{
    public Style? RouteStyle { get; set; }

    public override Style? SelectStyle(object item, DependencyObject container) =>
        item is TrayRoute ? RouteStyle : null;
}
