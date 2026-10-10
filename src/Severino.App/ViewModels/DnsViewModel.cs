using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Severino.App.Services;
using Severino.Contracts;
using Severino.Core.Configuration;
using Severino.Core.Dns;
using Severino.Core.Routes;

namespace Severino.App.ViewModels;

/// <summary>The DNS tab: Severino's entries, and the hosts lines from outside it.</summary>
public sealed partial class DnsViewModel : ObservableObject
{
    private static readonly TimeSpan UndoWindow = TimeSpan.FromSeconds(5);

    private readonly DnsService _dns;
    private readonly DnsSync _sync;
    private readonly ExternalHosts _external;
    private readonly ConfigService _config;
    private readonly DialogService _dialogs;
    private readonly DispatcherTimer _undoTimer;
    private (DnsEntry Entry, int Index)? _removed;

    public DnsViewModel(DnsService dns, DnsSync sync, ExternalHosts external, ConfigService config, DialogService dialogs)
    {
        _dns = dns;
        _sync = sync;
        _external = external;
        _config = config;
        _dialogs = dialogs;
        _undoTimer = new DispatcherTimer { Interval = UndoWindow };
        _undoTimer.Tick += (_, _) => DismissUndo();

        Reconcile();
        config.Changed += (_, _) => Dispatch(Reconcile);
        external.Changed += (_, _) => Dispatch(Reconcile);
        sync.StatusChanged += (_, _) => Dispatch(Reconcile);
    }

    public ObservableCollection<DnsRowViewModel> Own { get; } = [];
    public ObservableCollection<DnsRowViewModel> Outside { get; } = [];

    [ObservableProperty]
    public partial bool HasOwn { get; set; }

    [ObservableProperty]
    public partial bool HasOutside { get; set; }

    /// <summary>"4 entradas do Severino · 20 linhas de fora".</summary>
    [ObservableProperty]
    public partial string Summary { get; set; } = "";

    [ObservableProperty]
    public partial string Search { get; set; } = "";

    partial void OnSearchChanged(string value) => Reconcile();

    /// <summary>The Helper is missing or refused: the DNS block is not being written.</summary>
    [ObservableProperty]
    public partial string? Problem { get; set; }

    /// <summary>"2 entradas com IP público aguardam aprovação."</summary>
    [ObservableProperty]
    public partial string? PendingText { get; set; }

    [ObservableProperty]
    public partial string? UndoMessage { get; set; }

    [ObservableProperty]
    public partial string? Toast { get; set; }

    [RelayCommand]
    private async Task NewEntryAsync()
    {
        if (await _dialogs.EditDnsAsync(null) is { } saved)
            ShowToast($"{saved.Names[0]} pronto");
    }

    [RelayCommand]
    private async Task EditAsync(DnsRowViewModel row)
    {
        if (row.Entry is { } entry)
            await _dialogs.EditDnsAsync(entry);
        else if (row.Line is { Removed: false } line && await _dialogs.EditOutsideLineAsync(line))
            ShowToast("Linha alterada no hosts, com um comentário acima dela.");
    }

    [RelayCommand]
    private async Task RemoveAsync(DnsRowViewModel row)
    {
        if (row.Entry is { } entry)
        {
            var (routes, services) = DnsRules.UsedBy(entry.Names, _config.Current);
            if (routes.Count + services.Count > 0 && !await DialogService.ConfirmAsync("Remover entrada em uso",
                    $"{entry.Names[0]} é o destino de {UsedBy(routes.Count, services.Count)}. Sem ela, esses destinos deixam de resolver:\n\n" +
                    string.Join("\n", routes.Select(r => r.Domain).Concat(services.Select(s => s.Names[0]))),
                    "Remover mesmo assim"))
                return;
            _removed = _dns.Remove(entry.Id);
            if (_removed is null)
                return;
            UndoMessage = $"{entry.Names[0]} removido";
            _undoTimer.Stop();
            _undoTimer.Start();
            return;
        }

        if (row.Line is not { Removed: false } line)
            return;
        var after = $"{HostsText.NotePrefix} esta linha nao foi criada pelo Severino; removida em {DateTime.Now:yyyy-MM-dd HH:mm}. Antes: {line.Text.Trim()}\n# {line.Text.Trim()}";
        if (!await DialogService.ConfirmOutsideChangeAsync("Remover linha que não é do Severino", line.Text, after, "Comentar a linha"))
            return;
        var result = await _dns.RemoveOutsideAsync(line, CancellationToken.None);
        ShowToast(result.Ok ? "Linha comentada no hosts. Para trazê-la de volta, edite o hosts." : result.Error ?? "Não deu para alterar o hosts.");
    }

    [RelayCommand]
    private void Undo()
    {
        if (_removed is { } removed && !_dns.Restore(removed))
            ShowToast("Não deu para desfazer: os nomes já estão em outra entrada.");
        DismissUndo();
    }

    [RelayCommand]
    private void CopyName(DnsRowViewModel row)
    {
        Clipboard.SetText(row.Name);
        ShowToast("Nome copiado");
    }

    /// <summary>Approves every public address waiting, behind one UAC prompt.</summary>
    [RelayCommand]
    private async Task ApprovePendingAsync()
    {
        var pending = _sync.Status.PendingEntries;
        if (pending.Count == 0)
            return;
        if (await HelperApproval.ApproveAsync(pending) is { } error)
        {
            ShowToast(error);
            return;
        }
        await _sync.SyncAsync();
        ShowToast(pending.Count == 1 ? "Aprovado: já está no hosts." : "Aprovados: já estão no hosts.");
    }

    [RelayCommand]
    private static void OpenHosts() => HelperApproval.OpenHostsInNotepad();

    [RelayCommand]
    private void Reload() => _external.Refresh();

    private void Reconcile()
    {
        var config = _config.Current;
        var search = Search.Trim();
        bool Matches(IEnumerable<string> names, string address) =>
            search.Length == 0 || address.Contains(search, StringComparison.OrdinalIgnoreCase)
            || names.Any(n => n.Contains(search, StringComparison.OrdinalIgnoreCase));

        var pending = _sync.Status.PendingEntries;
        var own = config.DnsEntries.Where(e => Matches(e.Names, e.Address)).ToList();
        if (!own.Select(e => e.Id.ToString()).SequenceEqual(Own.Select(r => r.Key)))
        {
            Own.Clear();
            foreach (var entry in own)
                Own.Add(DnsRowViewModel.Own(entry, (row, enabled) => _dns.SetEnabled(row.Entry!.Id, enabled)));
        }
        else
        {
            for (var i = 0; i < own.Count; i++)
                Own[i].Update(own[i]);
        }
        foreach (var row in Own)
        {
            row.Pending = row.Entry!.Enabled && pending.Any(p => p.Address == row.Address && row.Names.Contains(p.Name));
            var (routes, services) = DnsRules.UsedBy(row.Names, config);
            row.UsedByText = routes.Count + services.Count == 0 ? null : $"Destino de {UsedBy(routes.Count, services.Count)}";
        }

        var outside = _external.Lines.Where(l => Matches(l.Names, l.Address)).ToList();
        if (!outside.Select(l => "line:" + l.Text).SequenceEqual(Outside.Select(r => r.Key)) || outside.Count != Outside.Count)
        {
            Outside.Clear();
            foreach (var line in outside)
                Outside.Add(DnsRowViewModel.Outside(line));
        }
        foreach (var row in Outside)
        {
            var (routes, services) = DnsRules.UsedBy(row.Names, config);
            row.UsedByText = routes.Count + services.Count == 0 ? null : $"Destino de {UsedBy(routes.Count, services.Count)}";
        }

        HasOwn = config.DnsEntries.Count > 0;
        HasOutside = _external.Lines.Count > 0;
        var ownCount = config.DnsEntries.Count;
        var outsideCount = _external.Lines.Count(l => !l.Removed);
        Summary = $"{Plural(ownCount, "entrada do Severino", "entradas do Severino")} · {Plural(outsideCount, "linha de fora", "linhas de fora")}";

        var status = _sync.Status;
        Problem = status.State switch
        {
            HostsSyncState.HelperUnavailable => "O serviço auxiliar não está respondendo: as entradas do Severino não estão sendo gravadas no hosts.",
            HostsSyncState.Failed => $"O hosts não foi atualizado: {status.Detail}",
            _ => null,
        };
        PendingText = pending.Count switch
        {
            0 => null,
            1 => $"{pending[0].Name} aponta para um IP público e só entra no hosts depois de aprovado. O Windows pede confirmação de administrador.",
            var n => $"{n} nomes apontam para IPs públicos e só entram no hosts depois de aprovados. O Windows pede confirmação de administrador uma vez.",
        };
    }

    private static string UsedBy(int routes, int services) =>
        string.Join(" e ", new[]
        {
            routes == 0 ? null : Plural(routes, "rota", "rotas"),
            services == 0 ? null : Plural(services, "serviço", "serviços"),
        }.OfType<string>());

    private static string Plural(int count, string one, string many) => count == 1 ? $"1 {one}" : $"{count} {many}";

    private void DismissUndo()
    {
        _undoTimer.Stop();
        _removed = null;
        UndoMessage = null;
    }

    private async void ShowToast(string message)
    {
        Toast = message;
        await Task.Delay(TimeSpan.FromSeconds(4));
        if (Toast == message)
            Toast = null;
    }

    private static void Dispatch(Action action) => Application.Current?.Dispatcher.BeginInvoke(action);
}
