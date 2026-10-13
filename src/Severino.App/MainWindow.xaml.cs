using Severino.App.ViewModels;
using Wpf.Ui.Controls;

namespace Severino.App;

public partial class MainWindow : FluentWindow
{
    public MainWindow(MainWindowViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }

    private void OnMoreClick(object sender, System.Windows.RoutedEventArgs e) => Views.MoreMenu.Open(sender);
}
