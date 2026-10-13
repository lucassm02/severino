using System.Windows;
using System.Windows.Controls;

namespace Severino.App.Views;

public partial class RequestsView : UserControl
{
    public RequestsView() => InitializeComponent();

    private void OnMoreClick(object sender, RoutedEventArgs e) => MoreMenu.Open(sender);
}