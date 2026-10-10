using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Severino.App.Services;
using Severino.Core.Configuration;
using Severino.Core.Routes;
using Severino.Proxy;

namespace Severino.App.ViewModels;

public enum StatusClass
{
    Informational,
    Success,
    Redirect,
    ClientError,
    ServerError,
}

/// <summary>One row of the request log.</summary>
public sealed partial class RequestItemViewModel(RequestEntry entry) : ObservableObject
{
    public RequestEntry Entry { get; } = entry;

    public string Time => Entry.Time.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
    public string Method => Entry.IsWebSocket ? "WS" : Entry.Method;
    public string Host => Entry.Host;
    public string Path => Entry.PathAndQuery;
    public int Status => Entry.Status;
    public bool FromProxy => Entry.FromProxy;
    public string Url => $"{Entry.Scheme}://{Entry.Authority}{Entry.PathAndQuery}";

    public StatusClass StatusClass => Entry.Status switch
    {
        < 200 => StatusClass.Informational,
        < 300 => StatusClass.Success,
        < 400 => StatusClass.Redirect,
        < 500 => StatusClass.ClientError,
        _ => StatusClass.ServerError,
    };

    public bool IsOpen => Entry.Duration is null;

    public string DurationText => Entry.Duration switch
    {
        null => "aberto",
        { TotalMilliseconds: < 1 } => "<1 ms",
        { TotalSeconds: < 1 } d => $"{d.TotalMilliseconds:0} ms",
        { TotalMinutes: < 1 } d => $"{d.TotalSeconds:0.0} s",
        var d => $"{(int)d.Value.TotalMinutes} min {d.Value.Seconds} s",
    };

    /// <summary>Picks up the duration once a WebSocket closes.</summary>
    public void Refresh() => OnPropertyChanged(nameof(DurationText));
}

public sealed record DomainFilter(string? Domain, string Label)
{
    public static readonly DomainFilter All = new(null, "Todos os domínios");

    public override string ToString() => Label;
}

/// <summary>The live request log: polls <see cref="RequestLog"/> and shows the newest first.</summary>
public sealed partial class RequestsViewModel : ObservableObject
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    private readonly RequestLog _log;
    private readonly ConfigService _config;
    private readonly ProxyServer _proxy;
    private readonly List<RequestItemViewModel> _open = [];
    private readonly List<RequestEntry> _held = [];
    private long _last = -1;

    public RequestsViewModel(RequestLog log, ConfigService config, ProxyServer proxy)
    {
        _log = log;
        _config = config;
        _proxy = proxy;

        View = CollectionViewSource.GetDefaultView(Items);
        View.Filter = item => SelectedDomain.Domain is not { } domain || ((RequestItemViewModel)item).Host == domain;

        RefreshDomains(config.Current.Routes);
        config.Changed += (_, c) => Dispatch(() => RefreshDomains(c.Routes));
        proxy.StatusChanged += (_, _) => Dispatch(() => OnPropertyChanged(nameof(EmptyText)));

        if (Application.Current is not null)
            new DispatcherTimer(PollInterval, DispatcherPriority.Background, (_, _) => Poll(), Application.Current.Dispatcher).Start();
    }

    /// <summary>Newest first, at most <see cref="RequestLog.Capacity"/>.</summary>
    public ObservableCollection<RequestItemViewModel> Items { get; } = [];

    public ICollectionView View { get; }

    public ObservableCollection<DomainFilter> Domains { get; } = [DomainFilter.All];

    [ObservableProperty]
    public partial DomainFilter SelectedDomain { get; set; } = DomainFilter.All;

    partial void OnSelectedDomainChanged(DomainFilter value)
    {
        View.Refresh();
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyText));
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PauseLabel), nameof(HeldText))]
    public partial bool IsPaused { get; set; }

    /// <summary>"12 novas" while paused.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeldText))]
    public partial int HeldCount { get; set; }

    public string PauseLabel => IsPaused ? "Retomar" : "Pausar";

    public string? HeldText => IsPaused && HeldCount > 0 ? $"{HeldCount} {(HeldCount == 1 ? "nova" : "novas")}" : null;

    public bool IsEmpty => View.IsEmpty;

    public string EmptyText => _proxy.Status.State == ProxyState.Running
        ? SelectedDomain.Domain is { } domain
            ? $"Nenhuma requisição para {domain} ainda."
            : "Abra uma das suas rotas no navegador e as requisições aparecem aqui."
        : "O proxy não está rodando. Veja a barra inferior.";

    [RelayCommand]
    private void TogglePause()
    {
        IsPaused = !IsPaused;
        if (!IsPaused)
            Show(_held);
    }

    [RelayCommand]
    private void Clear()
    {
        _log.Clear();
        _held.Clear();
        _open.Clear();
        Items.Clear();
        HeldCount = 0;
        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand]
    private static void CopyUrl(RequestItemViewModel? item)
    {
        if (item is not null)
            Clipboard.SetText(item.Url);
    }

    [RelayCommand]
    private static void Open(RequestItemViewModel? item)
    {
        if (item is not null && !item.Entry.IsWebSocket)
            Browser.Open(item.Url);
    }

    /// <summary>Takes what arrived since the last tick. Public for tests, which have no dispatcher.</summary>
    public void Poll()
    {
        var fresh = _log.Since(_last);
        if (fresh.Count > 0)
            _last = fresh[^1].Sequence;

        if (IsPaused)
        {
            _held.AddRange(fresh);
            if (_held.Count > RequestLog.Capacity)
                _held.RemoveRange(0, _held.Count - RequestLog.Capacity);
            HeldCount = _held.Count;
        }
        else
        {
            Show(fresh);
        }

        for (var i = _open.Count - 1; i >= 0; i--)
        {
            _open[i].Refresh();
            if (!_open[i].IsOpen)
                _open.RemoveAt(i);
        }
    }

    private void Show(IReadOnlyCollection<RequestEntry> entries)
    {
        foreach (var entry in entries)
        {
            var item = new RequestItemViewModel(entry);
            Items.Insert(0, item);
            if (item.IsOpen)
                _open.Add(item);
        }
        while (Items.Count > RequestLog.Capacity)
        {
            _open.Remove(Items[^1]);
            Items.RemoveAt(Items.Count - 1);
        }

        if (ReferenceEquals(entries, _held))
            _held.Clear();
        HeldCount = 0;
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void RefreshDomains(IEnumerable<RouteEntry> routes)
    {
        var domains = routes.Select(r => RouteRules.Normalize(r.Domain)).OfType<string>().Distinct().Order().ToList();
        var selected = SelectedDomain;
        while (Domains.Count > 1)
            Domains.RemoveAt(1);
        foreach (var domain in domains)
            Domains.Add(new DomainFilter(domain, domain));
        // A removed route's filter falls back to everything.
        SelectedDomain = Domains.FirstOrDefault(d => d.Domain == selected.Domain) ?? DomainFilter.All;
    }

    private static void Dispatch(Action action) => Application.Current?.Dispatcher.BeginInvoke(action);
}
