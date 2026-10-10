using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Severino.App.ViewModels;
using Severino.App.Views;
using Severino.Core.Configuration;
using Severino.Core.Domains;
using Severino.Core.Routes;
using FluentMessageBox = Wpf.Ui.Controls.MessageBox;
using FluentResult = Wpf.Ui.Controls.MessageBoxResult;

namespace Severino.App.Services;

public sealed class DialogService(RouteService routes, DomainInspector inspector, HttpsService https, Navigation navigation)
{
    /// <summary>Opens the route form; returns the saved route, or null when cancelled.</summary>
    public RouteEntry? EditRoute(RouteEntry? existing, bool isCopy = false)
    {
        var viewModel = new RouteEditorViewModel(routes, inspector, https, navigation, existing, isCopy);
        var window = new RouteEditorWindow(viewModel) { Owner = Application.Current.MainWindow };
        return window.ShowDialog() == true ? viewModel.Saved : null;
    }

    public static void ShowMessage(string title, string message) =>
        MessageBox.Show(Application.Current.MainWindow, message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    /// <summary>A yes/no question in the app's own style.</summary>
    public static Task<bool> ConfirmAsync(string title, string message, string confirmText) =>
        ShowAsync(title, Paragraph(message), confirmText);

    /// <summary>
    /// Explains Windows' security warning before it shows, with the name and thumbprint to check
    /// in it. <paramref name="lead"/> says why, when the user did not ask for it directly.
    /// </summary>
    public static Task<bool> ConfirmTrustAsync(string title, TrustPrompt prompt, string? lead = null)
    {
        var content = new StackPanel { MaxWidth = 460 };
        if (lead is not null)
            content.Children.Add(Paragraph(lead));
        content.Children.Add(Paragraph(
            "Em seguida o Windows mostra um Aviso de Segurança pedindo para confiar na CA do Severino. " +
            "É o aviso padrão do Windows para qualquer CA, por isso o tom alarmante. Confira se ele mostra:"));
        content.Children.Add(new TextBlock { Text = prompt.Name, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock
        {
            Text = $"Impressão digital (sha1): {prompt.Thumbprint}",
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            Margin = new Thickness(0, 2, 0, 12),
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(Paragraph($"Esta CA só consegue assinar: {HttpsService.DescribeNames(prompt.Names)}."));
        if (prompt.ReplacesCurrent)
            content.Children.Add(Paragraph("Depois o Windows pede para remover a CA antiga. Clique em Sim também: sem a chave, ela não serve para mais nada."));
        return ShowAsync(title, content, "Continuar");
    }

    /// <summary>An information box in the app's own style.</summary>
    public static async Task ShowInfoAsync(string title, string message)
    {
        var box = new FluentMessageBox
        {
            Title = title,
            Content = Paragraph(message),
            CloseButtonText = "OK",
            Owner = Application.Current.MainWindow,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        await box.ShowDialogAsync();
    }

    /// <summary>"Limpar tudo": confirmed, and whether to delete the routes and settings too.</summary>
    public static async Task<(bool Confirmed, bool DeleteData)> ConfirmCleanupAsync()
    {
        var deleteData = new CheckBox { Content = "Apagar também as rotas e configurações", Margin = new Thickness(0, 0, 0, 4) };
        var content = new StackPanel { MaxWidth = 460 };
        content.Children.Add(Paragraph(
            "O Severino tira do Windows tudo o que colocou: o bloco do arquivo hosts, a CA local e a inicialização automática. " +
            "Depois, o app fecha. O Windows pede confirmação para remover a CA."));
        content.Children.Add(Paragraph("Sem a caixa abaixo, suas rotas ficam guardadas para quando você abrir o Severino de novo."));
        content.Children.Add(deleteData);
        var confirmed = await ShowAsync("Limpar tudo", content, "Limpar e fechar");
        return (confirmed, deleteData.IsChecked == true);
    }

    /// <summary>Asks for a file to open; null when cancelled.</summary>
    public static string? PickOpenPath(string filter)
    {
        var dialog = new OpenFileDialog
        {
            Filter = filter,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FileName : null;
    }

    /// <summary>Asks where to save a file; null when cancelled.</summary>
    public static string? PickSavePath(string fileName, string filter)
    {
        var dialog = new SaveFileDialog
        {
            FileName = fileName,
            Filter = filter,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FileName : null;
    }

    private static async Task<bool> ShowAsync(string title, object content, string confirmText)
    {
        var box = new FluentMessageBox
        {
            Title = title,
            Content = content,
            PrimaryButtonText = confirmText,
            CloseButtonText = "Cancelar",
            Owner = Application.Current.MainWindow,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        return await box.ShowDialogAsync() == FluentResult.Primary;
    }

    private static TextBlock Paragraph(string text) =>
        new() { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12), MaxWidth = 460 };
}
