using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;
using Severino.App.Tray;
using Severino.App.ViewModels;

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

            var menu = icon.ContextMenu!;
            var items = menu.Items.OfType<MenuItem>().ToDictionary(i => (string)i.Header);
            Assert.Same(viewModel.ShowWindowCommand, items["Abrir Severino"].Command);
            Assert.Same(viewModel.TogglePauseCommand, items["Pausar"].Command);
            Assert.Same(viewModel.ExitCommand, items["Sair"].Command);
            Assert.Same(viewModel.ShowWindowCommand, icon.LeftClickCommand);
            Assert.Equal(Visibility.Collapsed, items["Nenhuma rota ligada"].Visibility);

            // Routes sit between the separators, each as a menu item that opens it.
            var route = Assert.Single(menu.Items.OfType<TrayRoute>());
            menu.IsOpen = true;
            menu.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            var container = (MenuItem)menu.ItemContainerGenerator.ContainerFromItem(route);
            Assert.Equal("meuapp.sev", container.Header);
            Assert.Same(route.Open, container.Command);
            menu.IsOpen = false;
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
        public ICommand TogglePauseCommand { get; } = new RelayCommand(() => { });
        public ICommand ExitCommand { get; } = new RelayCommand(() => { });
        public string PauseLabel => "Pausar";
        public string ToolTip => "Severino · tudo certo";
        public bool HasNoRoutes => false;
        public ObservableCollection<TrayRoute> Routes { get; } = [new("meuapp.sev", new RelayCommand(() => { }))];
    }
}
