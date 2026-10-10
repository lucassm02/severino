using Severino.App.ViewModels;
using Wpf.Ui.Controls;

namespace Severino.App.Views;

public partial class ImportServicesWindow : FluentWindow
{
    public ImportServicesWindow(ImportServicesViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        viewModel.CloseRequested += (_, imported) => DialogResult = imported;
        Loaded += async (_, _) => await viewModel.InitializeAsync();
    }
}
