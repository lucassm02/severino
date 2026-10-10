using CommunityToolkit.Mvvm.ComponentModel;
using Severino.Contracts;
using Severino.Core.Configuration;

namespace Severino.App.ViewModels;

/// <summary>
/// One row of the DNS tab: an entry of Severino's, or a hosts line from outside it. The two
/// look alike on purpose, except for the label that says whose the line is.
/// </summary>
public sealed partial class DnsRowViewModel : ObservableObject
{
    private readonly Action<DnsRowViewModel, bool>? _setEnabled;

    private DnsRowViewModel(DnsEntry? entry, HostsLine? line, Action<DnsRowViewModel, bool>? setEnabled)
    {
        Entry = entry;
        Line = line;
        _setEnabled = setEnabled;
    }

    public static DnsRowViewModel Own(DnsEntry entry, Action<DnsRowViewModel, bool> setEnabled) => new(entry, null, setEnabled);

    public static DnsRowViewModel Outside(HostsLine line) => new(null, line, null);

    /// <summary>Severino's entry; null for a line from outside.</summary>
    public DnsEntry? Entry { get; private set; }

    /// <summary>The hosts line from outside Severino; null for an entry of Severino's.</summary>
    public HostsLine? Line { get; }

    public bool IsOutside => Line is not null;
    public bool IsOwn => Entry is not null;
    public bool IsRemoved => Line?.Removed == true;
    public bool CanEdit => IsOwn || (IsOutside && !IsRemoved);

    public IReadOnlyList<string> Names => Entry?.Names ?? Line!.Names;
    public string Address => Entry?.Address ?? Line!.Address;
    public string Name => Names.Count > 0 ? Names[0] : "(sem nome)";
    public string? MoreNames => Names.Count switch { <= 1 => null, 2 => "+1 nome", var n => $"+{n - 1} nomes" };
    public string AllNames => string.Join(Environment.NewLine, Names);

    /// <summary>How far the address reaches, said plainly.</summary>
    public string ScopeText => !DnsAddress.TryClassify(Address, out _, out var scope) ? "endereço inválido"
        : scope switch
        {
            AddressScope.Loopback => "esta máquina",
            AddressScope.Private => "rede privada",
            _ => "IP público",
        };

    /// <summary>
    /// What Severino did to a line that is not its own, for a second label next to "fora do
    /// Severino"; null when it never touched it.
    /// </summary>
    public string? TouchedText => Line is null ? null
        : Line.Removed ? "comentada pelo Severino"
        : Line.Note is not null ? "editada pelo Severino"
        : null;

    public string? OriginText => Line?.Origin;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText), nameof(IsPending))]
    public partial bool Pending { get; set; }

    public bool IsPending => Pending;

    /// <summary>"1 rota", when routes go to the names; a link to them in the Rotas tab.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUsed), nameof(UsedByJoin))]
    public partial string? UsedByRoutes { get; set; }

    /// <summary>"3 serviços", when services go to the names; a link to them in the Serviços tab.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUsed), nameof(UsedByJoin))]
    public partial string? UsedByServices { get; set; }

    public bool IsUsed => UsedByRoutes is not null || UsedByServices is not null;

    /// <summary>" e " between the two links, when there are both.</summary>
    public string? UsedByJoin => UsedByRoutes is not null && UsedByServices is not null ? " e " : null;

    public void SetUsedBy(int routes, int services)
    {
        UsedByRoutes = routes switch { 0 => null, 1 => "1 rota", _ => $"{routes} rotas" };
        UsedByServices = services switch { 0 => null, 1 => "1 serviço", _ => $"{services} serviços" };
    }

    public string StateText => IsOutside ? (IsRemoved ? "Removida" : "No hosts")
        : Pending ? "Aguardando aprovação"
        : Entry!.Enabled ? "Ativa"
        : "Desligada";

    public bool Enabled
    {
        get => Entry?.Enabled ?? !IsRemoved;
        set
        {
            if (Entry is not null && value != Entry.Enabled)
                _setEnabled?.Invoke(this, value);
        }
    }

    /// <summary>The key a row keeps across refreshes: the entry id, or the line text.</summary>
    public string Key => Entry?.Id.ToString() ?? "line:" + Line!.Text;

    public void Update(DnsEntry entry)
    {
        Entry = entry;
        OnPropertyChanged(string.Empty);
    }
}
