using System.Windows;
using System.Windows.Controls;

namespace Severino.App.Views;

public partial class RoutesView : UserControl
{
    public RoutesView() => InitializeComponent();

    private void OnMoreClick(object sender, RoutedEventArgs e) => MoreMenu.Open(sender);
}
