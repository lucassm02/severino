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
}
