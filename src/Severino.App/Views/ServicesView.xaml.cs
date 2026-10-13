using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using Severino.App.Resources;
using Severino.App.Services;
using Severino.App.ViewModels;

namespace Severino.App.Views;

public partial class ServicesView : UserControl
{
    public ServicesView() => InitializeComponent();

    private void OnMoreClick(object sender, RoutedEventArgs e) => MoreMenu.Open(sender);

    /// <summary>One address opens at once; several get a menu under the name, one line each.</summary>
    private void OnNameClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Hyperlink { DataContext: ServiceItemViewModel item } link || link.Parent is not FrameworkElement owner)
            return;
        if (item.Links.Count == 1)
        {
            Browser.Open(item.Links[0]);
            return;
        }
        var menu = new ContextMenu { PlacementTarget = owner, Placement = PlacementMode.Bottom };
        foreach (var url in item.Links)
        {
            var entry = new MenuItem { Header = string.Format(Strings.Services_OpenIn, url) };
            entry.Click += (_, _) => Browser.Open(url);
            menu.Items.Add(entry);
        }
        menu.IsOpen = true;
    }
}
