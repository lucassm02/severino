using Severino.Contracts;
using Severino.Core.Configuration;
using Severino.Core.Helper;
using Severino.Core.Routes;

namespace Severino.Core.Dns;

/// <summary>A name the hosts resolves, offered as a destination.</summary>
/// <param name="Outside">From a hosts line outside Severino.</param>
public sealed record DnsDestination(string Name, string Address, bool Outside)
{
    public string Detail => Outside ? $"{Address}, no hosts fora do Severino" : Address;

    public override string ToString() => Name;
}

/// <param name="Pending">The new address is public and needs an approval first.</param>
public sealed record OutsideEditResult(bool Ok, string? Error = null, IReadOnlyList<HostEntry>? Pending = null)
{
    public bool NeedsApproval => Pending is { Count: > 0 };
}

/// <summary>
/// The DNS tab's work: Severino's own entries, kept in the config and written by <see cref="DnsSync"/>,
/// and the hosts lines outside Severino, changed through the Helper only when the person asks.
/// </summary>
public sealed class DnsService(ConfigService config, ExternalHosts external, IHelperClient helper)
{
    public IReadOnlyList<DnsEntry> Entries => config.Current.DnsEntries;

    /// <summary>
    /// Names that resolve through the hosts and can be a route's or a service's destination:
    /// Severino's enabled entries, then the lines from outside it.
    /// </summary>
    public IReadOnlyList<DnsDestination> Destinations() =>
    [
        .. config.Current.DnsEntries.Where(e => e.Enabled).SelectMany(e => e.Names.Select(n => new DnsDestination(n, e.Address, Outside: false))),
        .. external.Lines.Where(l => !l.Removed).SelectMany(l => l.Names.Select(n => new DnsDestination(n.ToLowerInvariant(), l.Address, Outside: true))),
    ];

    /// <summary>The name an address goes by in the hosts, for showing "gateway.k8s" instead of an IP.</summary>
    public string? NameFor(string address) =>
        Destinations().FirstOrDefault(d => d.Address == address)?.Name;

    public (DnsEntry? Normalized, string? Error) Validate(DnsEntry entry) =>
        DnsRules.Validate(entry, config.Current, external.Names);

    /// <summary>Adds the entry, or replaces the one with the same id.</summary>
    /// <exception cref="ArgumentException">The entry does not pass <see cref="Validate"/>.</exception>
    public DnsEntry Save(DnsEntry entry)
    {
        var (saved, error) = Validate(entry);
        if (saved is null)
            throw new ArgumentException(error, nameof(entry));
        config.Update(c =>
        {
            var entries = c.DnsEntries.ToList();
            var index = entries.FindIndex(e => e.Id == saved.Id);
            if (index >= 0)
                entries[index] = saved;
            else
                entries.Add(saved);
            return c with { DnsEntries = entries };
        });
        return saved;
    }

    public void SetEnabled(Guid id, bool enabled) =>
        config.Update(c => c with { DnsEntries = [.. c.DnsEntries.Select(e => e.Id == id ? e with { Enabled = enabled } : e)] });

    /// <summary>Removes the entry and returns it with its position, for <see cref="Restore"/>.</summary>
    public (DnsEntry Entry, int Index)? Remove(Guid id)
    {
        (DnsEntry, int)? removed = null;
        config.Update(c =>
        {
            var entries = c.DnsEntries.ToList();
            var index = entries.FindIndex(e => e.Id == id);
            if (index < 0)
                return c;
            removed = (entries[index], index);
            entries.RemoveAt(index);
            return c with { DnsEntries = entries };
        });
        return removed;
    }

    /// <summary>Puts a removed entry back, unless its names were taken meanwhile.</summary>
    public bool Restore((DnsEntry Entry, int Index) removed)
    {
        if (Validate(removed.Entry).Error is not null)
            return false;
        config.Update(c =>
        {
            var entries = c.DnsEntries.ToList();
            entries.Insert(Math.Min(removed.Index, entries.Count), removed.Entry);
            return c with { DnsEntries = entries };
        });
        return true;
    }

    /// <summary>
    /// Changes a line outside Severino to <paramref name="names"/> on <paramref name="address"/>.
    /// The Helper checks the line is still as read and leaves a note above it.
    /// </summary>
    public async Task<OutsideEditResult> EditOutsideAsync(HostsLine line, IReadOnlyList<string> names, string address, CancellationToken cancellationToken)
    {
        if (ValidateOutside(line, names, address) is { } error)
            return new(false, error);
        DnsAddress.TryClassify(address.Trim(), out var normalized, out _);
        return await SendAsync(HelperRequest.EditLine(line.Text, names.Select(n => new HostEntry(n.Trim(), normalized!))), cancellationToken);
    }

    /// <summary>Comments the line out, under a note: the person can bring it back by hand.</summary>
    public Task<OutsideEditResult> RemoveOutsideAsync(HostsLine line, CancellationToken cancellationToken) =>
        SendAsync(HelperRequest.EditLine(line.Text, []), cancellationToken);

    /// <summary>The same rules as an entry, except that the line keeps the names it already had.</summary>
    public string? ValidateOutside(HostsLine line, IReadOnlyList<string> names, string address)
    {
        var normalizedNames = new List<string>();
        foreach (var raw in names.Where(n => !string.IsNullOrWhiteSpace(n)))
        {
            if (!DomainName.TryNormalize(raw, out var name, out var reason, allowSingleLabel: true))
                return $"Nome inválido '{raw}': {reason}";
            normalizedNames.Add(name);
        }
        if (normalizedNames.Count == 0)
            return "Informe pelo menos um nome.";
        if (!DnsAddress.TryClassify(address?.Trim(), out _, out _))
            return "Informe um IPv4 ou IPv6 de um computador, como 10.0.0.8.";

        var own = line.Names.Select(n => n.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        var others = external.Lines.Where(l => l != line && !l.Removed).SelectMany(l => l.Names).Select(n => n.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        var c = config.Current;
        foreach (var name in normalizedNames.Where(n => !own.Contains(n)))
        {
            if (others.Contains(name))
                return $"{name} já está em outra linha do hosts.";
            if (DnsRules.Names(c.DnsEntries).Contains(name) || c.Services.Any(s => s.Names.Contains(name))
                || c.Routes.Any(r => RouteRules.Normalize(r.Domain) == name))
                return $"{name} já é do Severino (rota, serviço ou entrada DNS).";
        }
        return null;
    }

    private async Task<OutsideEditResult> SendAsync(HelperRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var response = await helper.SendAsync(request, cancellationToken);
            if (response.ProtocolVersion != HelperProtocol.Version)
                return new(false, $"O serviço auxiliar é de outra versão ({response.HelperVersion}). Reinstale o Severino.");
            external.Refresh();
            return response.Ok ? new(true) : new(false, response.Error, response.Pending);
        }
        catch (HelperUnavailableException ex)
        {
            return new(false, $"O serviço auxiliar não respondeu: {ex.Message}");
        }
    }
}
