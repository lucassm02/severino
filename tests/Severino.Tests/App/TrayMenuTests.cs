using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;
using Severino.App.Tray;

namespace Severino.Tests.App;

public sealed class TrayMenuTests
{
    [Fact]
    public void Menu_items_are_bound_to_the_view_model_commands()
    {
        RunOnSta(() =>
        {
            var resources = (ResourceDictionary)Application.LoadComponent(
                new Uri("/Severino;component/Tray/TrayIconResources.xaml", UriKind.Relative));
            var icon = (TaskbarIcon)resources["TrayIcon"];
            var viewModel = new FakeTrayViewModel();

            TrayService.Bind(icon, viewModel);
            // Let deferred bindings run, as the app's message loop would.
            icon.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

            var items = icon.ContextMenu!.Items.OfType<MenuItem>().ToDictionary(i => (string)i.Header);
            Assert.Same(viewModel.ShowWindowCommand, items["Abrir Severino"].Command);
            Assert.Same(viewModel.ExitCommand, items["Sair"].Command);
            Assert.Same(viewModel.ShowWindowCommand, icon.LeftClickCommand);
        });
    }

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
            throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    public sealed class FakeTrayViewModel
    {
        public ICommand ShowWindowCommand { get; } = new RelayCommand(() => { });
        public ICommand ExitCommand { get; } = new RelayCommand(() => { });
    }
}
