using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Severino.App.Resources;
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

    /// <summary>A service route's TCP connection has no HTTP status: it either reached the target or not.</summary>
    public bool IsTcp => Entry.Scheme == "tcp";

    public string StatusText => IsTcp ? (Entry.Status == 200 ? "ok" : Strings.Requests_TcpFailed) : Entry.Status.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public bool FromProxy => Entry.FromProxy;

    /// <summary>The address to copy: a URL for HTTP, "name:port" for a TCP connection.</summary>
    public string Url => IsTcp ? Entry.Authority : $"{Entry.Scheme}://{Entry.Authority}{Entry.PathAndQuery}";

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
        null => Strings.Requests_Open,
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
    public static readonly DomainFilter All = new(null, Strings.Requests_AllDomains);

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

    public string PauseLabel => IsPaused ? Strings.Requests_Resume : Strings.Requests_Pause;

    public string? HeldText => IsPaused && HeldCount > 0 ? (HeldCount == 1 ? Strings.Requests_HeldOne : string.Format(Strings.Requests_HeldMany, HeldCount)) : null;

    public bool IsEmpty => View.IsEmpty;

    public string EmptyText => _proxy.Status.State == ProxyState.Running
        ? SelectedDomain.Domain is { } domain
            ? string.Format(Strings.Requests_NoneFor, domain)
            : Strings.Requests_EmptyHint
        : Strings.Requests_ProxyDown;

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
        Selected = null;
        _log.Clear();
        _held.Clear();
        _open.Clear();
        Items.Clear();
        HeldCount = 0;
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>The row whose details are open in the side panel.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetails))]
    public partial RequestItemViewModel? Selected { get; set; }

    [ObservableProperty]
    public partial RequestDetailsViewModel? Details { get; private set; }

    public bool HasDetails => Selected is not null;

    /// <summary>The panel's tab, kept while walking the list.</summary>
    [ObservableProperty]
    public partial int DetailsTab { get; set; }

    /// <summary>The tab chosen on an HTTP row; a TCP connection only has General, and the choice waits for the next HTTP one.</summary>
    private int _chosenTab;

    partial void OnDetailsTabChanged(int value)
    {
        if (Details?.IsHttp == true)
            _chosenTab = value;
    }

    partial void OnSelectedChanged(RequestItemViewModel? value)
    {
        Details = value is null ? null : new RequestDetailsViewModel(value, RouteName(value.Entry.Details?.RouteId), CaptureBodies);
        DetailsTab = Details?.IsHttp == false ? 0 : _chosenTab;
    }

    /// <summary>The log's switch, here so the tab shows it.</summary>
    public bool CaptureBodies
    {
        get => _log.CaptureBodies;
        set
        {
            if (_log.CaptureBodies == value)
                return;
            _log.CaptureBodies = value;
            OnPropertyChanged();
        }
    }

    [RelayCommand]
    private void ToggleCapture() => CaptureBodies = !CaptureBodies;

    [RelayCommand]
    private void CloseDetails() => Selected = null;

    [RelayCommand]
    private void CopyCurl(CurlShell shell)
    {
        if (Details is { } details)
            Clipboard.SetText(RequestExport.ToCurl(details.Item.Entry, shell));
    }

    [RelayCommand]
    private async Task RepeatAsync()
    {
        if (Details is not { } details)
            return;
        if (!details.CanReplay)
        {
            details.Message = Strings.Requests_CannotRepeat;
            return;
        }
        try
        {
            var https = _proxy.HttpsStatus.State == ProxyState.Running ? _proxy.HttpsStatus.Port : (int?)null;
            var status = await RequestExport.ReplayAsync(details.Item.Entry, _proxy.Status.Port, https);
            details.Message = string.Format(Strings.Requests_Repeated, status);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            details.Message = string.Format(Strings.Requests_RepeatFailed, ex.Message);
        }
    }

    /// <summary>The rows on the list, as filtered, into a HAR file.</summary>
    [RelayCommand]
    private void ExportHar()
    {
        if (DialogService.PickSavePath($"severino-{DateTime.Now:yyyyMMdd-HHmmss}.har", Strings.Requests_HarFilter) is not { } path)
            return;
        var entries = View.Cast<RequestItemViewModel>().Select(i => i.Entry);
        var version = (typeof(RequestsViewModel).Assembly.GetName().Version ?? new Version(0, 0, 0)).ToString(3);
        try
        {
            File.WriteAllText(path, RequestExport.ToHar(entries, version));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DialogService.ShowMessage(Strings.Requests_ExportHar, string.Format(Strings.Common_CouldNotWriteFile, ex.Message));
        }
    }

    private string? RouteName(Guid? id) =>
        id is { } routeId && _config.Current.Routes.FirstOrDefault(r => r.Id == routeId) is { } route ? route.Domain + route.Path : null;

    [RelayCommand]
    private static void CopyText(string? text)
    {
        if (!string.IsNullOrEmpty(text))
            Clipboard.SetText(text);
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
        if (item is not null && !item.Entry.IsWebSocket && !item.IsTcp)
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
