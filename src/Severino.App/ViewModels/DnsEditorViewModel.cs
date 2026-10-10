using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

    public DnsEditorViewModel(DnsService dns, DnsSync sync, DnsEntry? entry, HostsLine? line = null)
    {
        _dns = dns;
        _sync = sync;
        _entry = entry;
        _line = line;
        NamesText = string.Join(Environment.NewLine, entry?.Names ?? line?.Names ?? []);
        Address = entry?.Address ?? line?.Address ?? "";
        Notes = entry?.Notes ?? "";
    }

    public bool IsOutside => _line is not null;
    public bool IsOwn => !IsOutside;

    public string Title => IsOutside ? "Editar linha que não é do Severino" : _entry is null ? "Nova entrada DNS" : "Editar entrada DNS";
    public string SaveLabel => IsOutside ? "Revisar e alterar o hosts" : _entry is null ? "Criar" : "Salvar";

    /// <summary>Fixed at the top of the form for a line from outside.</summary>
    public string OutsideWarning =>
        "Ela não foi criada pelo Severino. Ao salvar, ele altera o arquivo e deixa um comentário acima dela, dizendo que ela não era dele, o que mudou e quando.";

    public string? OriginText => _line?.Origin is { } origin ? $"No hosts, perto de: {origin}" : null;

    public string? LineText => _line is null ? null : $"Linha atual: {_line.Text.Trim()}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScopeHint))]
    public partial string Address { get; set; }

    [ObservableProperty]
    public partial string NamesText { get; set; }

    [ObservableProperty]
    public partial string Notes { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool IsBusy { get; set; }

    /// <summary>What the address means for the hosts, as the person types.</summary>
    public string? ScopeHint => !DnsAddress.TryClassify(Address?.Trim(), out _, out var scope) ? null
        : scope switch
        {
            AddressScope.Loopback => "Esta máquina.",
            AddressScope.Private => "Rede privada: vai para o hosts na hora.",
            _ => "IP público: depois de salvar, o Windows pede confirmação de administrador para aprovar este nome neste IP.",
        };

    /// <summary>Said when a name is a wildcard, since it needs an approval too.</summary>
    public string? WildcardHint => IsOwn && Names.Any(DomainName.IsWildcard)
        ? "Curinga: vale para todo nome abaixo dele, respondido pelo serviço auxiliar. Depois de salvar, o Windows pede confirmação de administrador."
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
        var draft = (_entry ?? new DnsEntry()) with { Names = Names, Address = Address.Trim(), Notes = Notes };
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
                Error = $"{approvalError} A entrada foi salva e fica aguardando aprovação.";
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
        if (!await DialogService.ConfirmOutsideChangeAsync("Alterar linha que não é do Severino", line.Text, after, "Alterar o hosts"))
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
