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

    /// <summary>A severino.json dropped on the window: its preview, then applying it if confirmed.</summary>
    private void OnDragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = ProjectFile(e) is not null ? System.Windows.DragDropEffects.Copy : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, System.Windows.DragEventArgs e)
    {
        if (ProjectFile(e) is { } path && DataContext is MainWindowViewModel { Projects: { } projects })
            _ = projects.OpenAsync(path);
    }

    private static string? ProjectFile(System.Windows.DragEventArgs e) =>
        e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] { Length: 1 } files
        && files[0].EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? files[0] : null;
}
