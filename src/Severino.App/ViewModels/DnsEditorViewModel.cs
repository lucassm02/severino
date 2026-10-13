using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Severino.App.Resources;
using Severino.App.Services;
using Severino.Contracts;
using Severino.Core.Configuration;
using Severino.Core.Dns;

namespace Severino.App.ViewModels;

/// <summary>
/// The DNS form, for an entry of Severino's or for a hosts line from outside it. For a line
/// from outside, the warning never leaves the top of the form, and saving shows the change first.
/// </summary>
public sealed partial class DnsEditorViewModel : ObservableObject
{
    private readonly DnsService _dns;
    private readonly DnsSync _sync;
    private readonly DnsEntry? _entry;
    private readonly HostsLine? _line;

    /// <param name="draft">For a new entry, what to start with, such as the name and IP of a route form.</param>
    public DnsEditorViewModel(DnsService dns, DnsSync sync, DnsEntry? entry, HostsLine? line = null, DnsEntry? draft = null)
    {
        _dns = dns;
        _sync = sync;
        _entry = entry;
        _line = line;
        var start = entry ?? draft;
        NamesText = string.Join(System.Environment.NewLine, start?.Names ?? line?.Names ?? []);
        Address = start?.Address ?? line?.Address ?? "";
        Notes = start?.Notes ?? "";
        Environment = start?.Environment ?? "";
        EnvironmentSuggestions = dns.EnvironmentNames;
    }

    public bool IsOutside => _line is not null;
    public bool IsOwn => !IsOutside;

    public string Title => IsOutside ? Strings.DnsEditor_EditOutside : _entry is null ? Strings.DnsEditor_New : Strings.DnsEditor_Edit;
    public string SaveLabel => IsOutside ? Strings.DnsEditor_ReviewAndChange : _entry is null ? Strings.Common_Create : Strings.Common_Save;

    /// <summary>Fixed at the top of the form for a line from outside.</summary>
    public string OutsideWarning =>
        Strings.DnsEditor_OutsideWarning;

    public string? OriginText => _line?.Origin is { } origin ? string.Format(Strings.DnsEditor_Near, origin) : null;

    public string? LineText => _line is null ? null : string.Format(Strings.DnsEditor_CurrentLine, _line.Text.Trim());

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScopeHint))]
    public partial string Address { get; set; }

    [ObservableProperty]
    public partial string NamesText { get; set; }

    [ObservableProperty]
    public partial string Notes { get; set; }
    /// <summary>Empty: counts in every environment.</summary>
    [ObservableProperty]
    public partial string Environment { get; set; } = "";

    public IReadOnlyList<string> EnvironmentSuggestions { get; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool IsBusy { get; set; }

    /// <summary>What the address means for the hosts, as the person types.</summary>
    public string? ScopeHint => !DnsAddress.TryClassify(Address?.Trim(), out _, out var scope) ? null
        : scope switch
        {
            AddressScope.Loopback => Strings.DnsEditor_ScopeLoopback,
            AddressScope.Private => Strings.DnsEditor_ScopePrivate,
            _ => Strings.DnsEditor_ScopePublic,
        };

    /// <summary>Said when a name is a wildcard, since it needs an approval too.</summary>
    public string? WildcardHint => IsOwn && Names.Any(DomainName.IsWildcard)
        ? Strings.DnsEditor_Wildcard
        : null;

    partial void OnNamesTextChanged(string value) => OnPropertyChanged(nameof(WildcardHint));

    public DnsEntry? Saved { get; private set; }

    public event EventHandler<bool>? CloseRequested;

    private IReadOnlyList<string> Names =>
        NamesText.Split(['\n', '\r', ',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        Error = null;
        IsBusy = true;
        try
        {
            if (_line is not null)
                await SaveOutsideAsync(_line);
            else
                await SaveOwnAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanSave() => !IsBusy;

    private async Task SaveOwnAsync()
    {
        var draft = (_entry ?? new DnsEntry()) with { Names = Names, Address = Address.Trim(), Notes = Notes, Environment = (Environment ?? "").Trim() };
        var (normalized, error) = _dns.Validate(draft);
        if (normalized is null)
        {
            Error = error;
            return;
        }
        Saved = _dns.Save(normalized);

        // A public address or a wildcard waits for an approval: ask for it right away.
        if (normalized.Enabled && DnsAddress.TryClassify(normalized.Address, out _, out var scope)
            && (scope == AddressScope.Public || normalized.Names.Any(DomainName.IsWildcard)))
        {
            var entries = normalized.Names.Select(n => new HostEntry(n, normalized.Address)).ToList();
            if (await HelperApproval.ApproveAsync(entries) is { } approvalError)
            {
                // Saved anyway: the DNS tab keeps offering the approval.
                Error = string.Format(Strings.DnsEditor_SavedPending, approvalError);
                await Task.Delay(1500);
            }
            await _sync.SyncAsync();
        }
        CloseRequested?.Invoke(this, true);
    }

    private async Task SaveOutsideAsync(HostsLine line)
    {
        if (_dns.ValidateOutside(line, Names, Address) is { } error)
        {
            Error = error;
            return;
        }
        DnsAddress.TryClassify(Address.Trim(), out var address, out _);
        var after = $"{HostsText.NotePrefix} esta linha nao foi criada pelo Severino; editada em {DateTime.Now:yyyy-MM-dd HH:mm}. Antes: {line.Text.Trim()}\n" +
            HostsText.Render(address!, Names);
        if (!await DialogService.ConfirmOutsideChangeAsync(Strings.DnsEditor_ChangeOutside, line.Text, after, Strings.DnsEditor_ChangeHosts))
            return;

        var result = await _dns.EditOutsideAsync(line, Names, Address, CancellationToken.None);
        if (result.NeedsApproval)
        {
            if (await HelperApproval.ApproveAsync(result.Pending!) is { } approvalError)
            {
                Error = approvalError;
                return;
            }
            result = await _dns.EditOutsideAsync(line, Names, Address, CancellationToken.None);
        }
        if (!result.Ok)
        {
            Error = result.Error;
            return;
        }
        CloseRequested?.Invoke(this, true);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);
}
