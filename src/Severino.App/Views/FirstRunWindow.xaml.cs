using Severino.App.ViewModels;
using Wpf.Ui.Controls;

namespace Severino.App.Views;

public partial class FirstRunWindow : FluentWindow
{
    private bool _finished;
    private bool _closing;

    public FirstRunWindow(FirstRunViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FirstRunViewModel.IsRouteStep) && viewModel.IsRouteStep)
                Dispatcher.BeginInvoke(Form.FocusDomain);
        };

        // Finished by a button: close. Finished because the X is already closing: nothing to do.
        viewModel.Finished += (_, _) =>
        {
            _finished = true;
            if (!_closing)
                Close();
        };
        // The X counts as "Pular", so the wizard does not come back on every start.
        Closing += (_, _) =>
        {
            _closing = true;
            if (!_finished)
                viewModel.SkipCommand.Execute(null);
        };
    }
}
