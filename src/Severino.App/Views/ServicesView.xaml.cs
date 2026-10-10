using System.Windows;
using System.Windows.Controls;

namespace Severino.App.Views;

public partial class ServicesView : UserControl
{
    public ServicesView() => InitializeComponent();

    private void OnMoreClick(object sender, RoutedEventArgs e) => MoreMenu.Open(sender);
}
