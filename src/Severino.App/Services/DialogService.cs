using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Severino.App.ViewModels;
using Severino.App.Views;
using Severino.Core.Configuration;
using Severino.Contracts;
using Severino.Core.Discovery;
using Severino.Core.Dns;
using Severino.Core.Domains;
using Severino.Core.Routes;
using FluentMessageBox = Wpf.Ui.Controls.MessageBox;
using FluentResult = Wpf.Ui.Controls.MessageBoxResult;

namespace Severino.App.Services;

/// <param name="IngressRoutes">Web routes made from Ingress hosts.</param>
public sealed record ImportOutcome(IReadOnlyList<PlannedService> Services, IReadOnlyList<string> IngressRoutes);

public sealed class DialogService(RouteService routes, ServiceRouteService services, ServiceDiscovery discovery, ConfigService config, DomainInspector inspector, HttpsService https, Navigation navigation,
    DnsService? dns = null, DnsSync? dnsSync = null)
{
    /// <summary>The DNS form for an entry of Severino's; returns it saved, or null when cancelled.</summary>
    public Task<DnsEntry?> EditDnsAsync(DnsEntry? existing)
    {
        var viewModel = new DnsEditorViewModel(dns!, dnsSync!, existing);
        var window = new DnsEditorWindow(viewModel) { Owner = Application.Current.MainWindow };
        return Task.FromResult(window.ShowDialog() == true ? viewModel.Saved : null);
    }

    /// <summary>The DNS form for a hosts line from outside Severino; true when the line was changed.</summary>
    public Task<bool> EditOutsideLineAsync(HostsLine line)
    {
        var viewModel = new DnsEditorViewModel(dns!, dnsSync!, null, line);
        var window = new DnsEditorWindow(viewModel) { Owner = Application.Current.MainWindow };
        return Task.FromResult(window.ShowDialog() == true);
    }

    /// <summary>
    /// Before Severino changes a hosts line that is not its own: says so, and shows the line as it
    /// is and as it will be, comment included.
    /// </summary>
    public static Task<bool> ConfirmOutsideChangeAsync(string title, string before, string after, string confirmText)
    {
        var content = new StackPanel { MaxWidth = 560 };
        content.Children.Add(Paragraph(
            "Esta linha não foi criada pelo Severino. Ele vai alterar o hosts mesmo assim, porque você pediu, e deixar um comentário acima dela."));
        content.Children.Add(new TextBlock { Text = "Como está:", FontWeight = FontWeights.SemiBold });
        content.Children.Add(Code(before.Trim()));
        content.Children.Add(new TextBlock { Text = "Como vai ficar:", FontWeight = FontWeights.SemiBold });
        content.Children.Add(Code(after.Trim()));
        return ShowAsync(title, content, confirmText);
    }

    private static TextBlock Code(string text) => new()
    {
        Text = text,
        FontFamily = new FontFamily("Cascadia Mono, Consolas"),
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 4, 0, 12),
    };

    /// <summary>The "Importar serviços" window; returns what was imported, or null when cancelled.</summary>
    public ImportOutcome? ImportServices()
    {
        var viewModel = new ImportServicesViewModel(discovery, services, config, dns?.Destinations(), routes);
        var window = new ImportServicesWindow(viewModel) { Owner = Application.Current.MainWindow };
        return window.ShowDialog() == true ? new ImportOutcome(viewModel.Result ?? [], viewModel.IngressRoutes) : null;
    }

    /// <summary>The service form; returns the saved route, or null when cancelled.</summary>
    public ServiceRoute? EditService(ServiceRoute? existing)
    {
        var viewModel = new ServiceEditorViewModel(services, existing, dns?.Destinations());
        var window = new ServiceEditorWindow(viewModel) { Owner = Application.Current.MainWindow };
        return window.ShowDialog() == true ? viewModel.Saved : null;
    }

    /// <summary>Opens the route form; returns the saved route, or null when cancelled.</summary>
    /// <param name="group">For a new route, the group it starts in.</param>
    public RouteEntry? EditRoute(RouteEntry? existing, bool isCopy = false, string? group = null)
    {
        var viewModel = new RouteEditorViewModel(routes, inspector, https, navigation, existing, isCopy, dns?.Destinations());
        if (group is not null)
            viewModel.Group = group;
        var window = new RouteEditorWindow(viewModel) { Owner = Application.Current.MainWindow };
        return window.ShowDialog() == true ? viewModel.Saved : null;
    }

    /// <summary>Asks for one line of text; null when cancelled.</summary>
    public static async Task<string?> PromptAsync(string title, string message, string initial, string confirmText)
    {
        var box = new Wpf.Ui.Controls.TextBox { Text = initial, Margin = new Thickness(0, 0, 0, 4) };
        var content = new StackPanel { MaxWidth = 460 };
        content.Children.Add(Paragraph(message));
        content.Children.Add(box);
        box.Loaded += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };
        return await ShowAsync(title, content, confirmText) ? box.Text : null;
    }

    /// <summary>The first-run wizard, over the main window.</summary>
    public static void ShowFirstRun(FirstRunViewModel viewModel) =>
        new FirstRunWindow(viewModel) { Owner = Application.Current.MainWindow }.ShowDialog();

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
            "O Severino tira do Windows tudo o que colocou: os blocos do arquivo hosts (rotas e DNS), a CA local, a inicialização automática, as exceções de proxy que ele adicionou e os nomes nas distros do WSL que estão rodando. Linhas do hosts que não são dele ficam como estão. " +
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
