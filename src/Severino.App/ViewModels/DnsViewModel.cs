using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Severino.App.Resources;
using Severino.App.Services;
using Severino.Contracts;
using Severino.Core.Configuration;
using Severino.Core.Dns;
using Severino.Core.Routes;

namespace Severino.App.ViewModels;

/// <summary>The DNS tab: Severino's entries, and the hosts lines from outside it.</summary>
public sealed partial class DnsViewModel : ListPageViewModel
{
    private readonly DnsService _dns;
    private readonly DnsSync _sync;
    private readonly ExternalHosts _external;
    private readonly ConfigService _config;
    private readonly DialogService _dialogs;
    private readonly Navigation? _navigation;

    public DnsViewModel(DnsService dns, DnsSync sync, ExternalHosts external, ConfigService config, DialogService dialogs, Navigation? navigation = null)
    {
        _dns = dns;
        _sync = sync;
        _external = external;
        _config = config;
        _dialogs = dialogs;
        _navigation = navigation;

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

    protected override void OnSearch() => Reconcile();

    /// <summary>The Helper is missing or refused: the DNS block is not being written.</summary>
    [ObservableProperty]
    public partial string? Problem { get; set; }

    /// <summary>"2 entradas com IP público aguardam aprovação."</summary>
    [ObservableProperty]
    public partial string? PendingText { get; set; }

    [RelayCommand]
    private async Task NewEntryAsync()
    {
        if (await _dialogs.EditDnsAsync(null) is { } saved)
            ShowToast(string.Format(Strings.Routes_Ready, saved.Names[0]));
    }

    [RelayCommand]
    private async Task EditAsync(DnsRowViewModel row)
    {
        if (row.Entry is { } entry)
            await _dialogs.EditDnsAsync(entry);
        else if (row.Line is { Removed: false } line && await _dialogs.EditOutsideLineAsync(line))
            ShowToast(Strings.Dns_LineChanged);
    }

    [RelayCommand]
    private async Task RemoveAsync(DnsRowViewModel row)
    {
        if (row.Entry is { } entry)
        {
            var (routes, services) = DnsRules.UsedBy(entry.Names, _config.Current);
            if (routes.Count + services.Count > 0 && !await DialogService.ConfirmAsync(Strings.Dns_RemoveInUse,
                    string.Format(Strings.Dns_RemoveInUseConfirm, entry.Names[0], UsedBy(routes.Count, services.Count),
                        string.Join("\n", routes.Select(r => r.Domain).Concat(services.Select(s => s.Names[0])))),
                    Strings.Dns_RemoveAnyway))
                return;
            if (_dns.Remove(entry.Id) is not { } removed)
                return;
            ShowUndo(string.Format(Strings.Common_Removed, entry.Names[0]), () => _dns.Restore(removed) ? null : Strings.Dns_UndoTaken);
            return;
        }

        if (row.Line is not { Removed: false } line)
            return;
        var after = $"{HostsText.NotePrefix} esta linha nao foi criada pelo Severino; removida em {DateTime.Now:yyyy-MM-dd HH:mm}. Antes: {line.Text.Trim()}\n# {line.Text.Trim()}";
        if (!await DialogService.ConfirmOutsideChangeAsync(Strings.Dns_RemoveOutside, line.Text, after, Strings.Dns_CommentLine))
            return;
        var result = await _dns.RemoveOutsideAsync(line, CancellationToken.None);
        ShowToast(result.Ok ? Strings.Dns_LineCommented : result.Error ?? Strings.Dns_CouldNotChangeHosts);
    }

    /// <summary>
    /// An entry becomes a route: the name goes through the proxy to the same address, gaining
    /// HTTPS from the local CA and the request log. The other names stay in the entry.
    /// </summary>
    [RelayCommand]
    private void MakeRoute(DnsRowViewModel row)
    {
        if (row.Entry is not { } entry)
            return;
        if (entry.Names.FirstOrDefault(n => n.Contains('.')) is not { } name)
        {
            ShowToast(Strings.Dns_RouteNeedsDot);
            return;
        }

        // The name is freed first, so the route form accepts it; cancelling puts the entry back.
        if (_dns.Remove(entry.Id) is not { } removed)
            return;
        var host = entry.Address.Contains(':') ? $"[{entry.Address}]" : entry.Address;
        var draft = new RouteEntry { Domain = name, Target = $"http://{host}:80" };
        if (_dialogs.EditRoute(draft, isCopy: true) is null)
        {
            _dns.Restore(removed);
            return;
        }
        var rest = entry.Names.Where(n => n != name).ToList();
        if (rest.Count > 0)
            _dns.Save(entry with { Names = rest });
        ShowToast(string.Format(Strings.Dns_NowRoute, name), Strings.Common_SeeInRoutes, () => _navigation?.Show(AppTab.Routes, name));
    }

    /// <summary>The routes that go to this name, in the Rotas tab.</summary>
    [RelayCommand]
    private void ShowRoutes(DnsRowViewModel row) => _navigation?.Show(AppTab.Routes, row.Name);

    /// <summary>The services that go to this name, in the Serviços tab.</summary>
    [RelayCommand]
    private void ShowServices(DnsRowViewModel row) => _navigation?.Show(AppTab.Services, row.Name);

    [RelayCommand]
    private void CopyName(DnsRowViewModel row)
    {
        Clipboard.SetText(row.Name);
        ShowToast(Strings.Services_NameCopied);
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
        ShowToast(pending.Count == 1 ? Strings.Dns_ApprovedOne : Strings.Dns_ApprovedMany);
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
            row.OtherEnvironment = !Environments.IsActive(row.Entry!.Environment, config);
            row.Pending = row.Entry!.Enabled && pending.Any(p => p.Address == row.Address && row.Names.Contains(p.Name));
            var (routes, services) = DnsRules.UsedBy(row.Names, config);
            row.SetUsedBy(routes.Count, services.Count);
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
            row.SetUsedBy(routes.Count, services.Count);
        }

        HasOwn = config.DnsEntries.Count > 0;
        HasOutside = _external.Lines.Count > 0;
        var ownCount = config.DnsEntries.Count;
        var outsideCount = _external.Lines.Count(l => !l.Removed);
        Summary = $"{Plural(ownCount, Strings.Dns_OwnCountOne, Strings.Dns_OwnCountMany)} · {Plural(outsideCount, Strings.Dns_OutsideCountOne, Strings.Dns_OutsideCountMany)}";

        var status = _sync.Status;
        Problem = status.State switch
        {
            HostsSyncState.HelperUnavailable => Strings.Dns_HelperDown,
            HostsSyncState.Failed => string.Format(Strings.Dns_HostsFailed, status.Detail),
            _ => null,
        };
        var pendingNames = pending.Select(p => p.Name).Distinct().ToList();
        PendingText = pendingNames.Count switch
        {
            0 => null,
            1 => string.Format(Strings.Dns_PendingOne, pendingNames[0]),
            var n => string.Format(Strings.Dns_PendingMany, n),
        };
    }

    private static string UsedBy(int routes, int services) =>
        string.Join(Strings.Common_And, new[]
        {
            routes == 0 ? null : Plural(routes, Strings.Count_Route, Strings.Count_Routes),
            services == 0 ? null : Plural(services, Strings.Count_Service, Strings.Count_Services),
        }.OfType<string>());

}
