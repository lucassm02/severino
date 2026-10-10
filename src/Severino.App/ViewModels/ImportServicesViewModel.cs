using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Severino.Core.Configuration;
using Severino.Core.Discovery;
using Severino.Core.Routes;

namespace Severino.App.ViewModels;

/// <summary>Where to look: Windows, a WSL distro, or text pasted by hand.</summary>
/// <param name="Source">Null for the paste option.</param>
/// <param name="Stopped">A distro that is not running; looking starts it.</param>
public sealed record SourceOption(CommandSource? Source, bool Stopped = false)
{
    public bool IsPaste => Source is null;

    public string Label => Source is null ? "Colar saída de comando"
        : Stopped ? $"{Source} (parada; procurar inicia a distro)"
        : Source.ToString();

    public override string ToString() => Label;
}

/// <summary>What one source answered, kept so switching back does not ask again.</summary>
public sealed record SourceScan(KubernetesResult? Kubernetes, DockerResult? Docker);

/// <summary>The "Importar serviços" window.</summary>
public sealed partial class ImportServicesViewModel : ObservableObject
{
    public const string AllNamespaces = "Todos os namespaces";

    private readonly ServiceDiscovery _discovery;
    private readonly ServiceRouteService _services;
    private readonly ConfigService _config;
    private readonly Dictionary<SourceOption, SourceScan> _scans = [];
    private List<CandidateItemViewModel> _all = [];
    private CancellationTokenSource? _loading;
    private bool _batch;
    private readonly IReadOnlyList<Core.Dns.DnsDestination> _destinations;

    /// <summary>DNS name → node IP, for the nodes the hosts has a name for.</summary>
    private Dictionary<string, string> _nodeNames = [];

    /// <summary>The node the person picked last, by IP or by name; kept across reloads.</summary>
    private string? _nodeChoice;

    public ImportServicesViewModel(ServiceDiscovery discovery, ServiceRouteService services, ConfigService config,
        IReadOnlyList<Core.Dns.DnsDestination>? destinations = null)
    {
        _discovery = discovery;
        _services = services;
        _config = config;
        _destinations = destinations ?? [];
        UseKubernetes = config.Current.State.ImportKubernetes;
        UseDocker = config.Current.State.ImportDocker;
        _toolsReady = true;
    }

    /// <summary>False while the constructor reads the saved choice, so reading it does not save half of it.</summary>
    private readonly bool _toolsReady;

    /// <summary>Ask kubectl, and show what it found. Off, a slow or absent cluster costs nothing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoTool))]
    public partial bool UseKubernetes { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoTool))]
    public partial bool UseDocker { get; set; }

    public bool NoTool => !UseKubernetes && !UseDocker;

    partial void OnUseKubernetesChanged(bool value) => OnToolChanged();

    partial void OnUseDockerChanged(bool value) => OnToolChanged();

    /// <summary>Remembers the choice, asks the tool just turned on if it was not asked yet, and filters the list.</summary>
    private void OnToolChanged()
    {
        if (!_toolsReady)
            return;
        _config.Update(c => c with { State = c.State with { ImportKubernetes = UseKubernetes, ImportDocker = UseDocker } });
        if (SelectedSource is not { IsPaste: false } source)
            return;
        _scans.TryGetValue(source, out var scan);
        var kubernetes = UseKubernetes && scan?.Kubernetes is null;
        var docker = UseDocker && scan?.Docker is null;
        if (kubernetes || docker)
            _ = LoadAsync(source, SelectedNode, kubernetes, docker);
        else
            ShowScan(scan);
    }

    public ObservableCollection<SourceOption> Sources { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPaste), nameof(IsDiscovery))]
    public partial SourceOption? SelectedSource { get; set; }

    public bool IsPaste => SelectedSource?.IsPaste == true;
    public bool IsDiscovery => SelectedSource is { IsPaste: false };

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    /// <summary>"Kubernetes · contexto · 119 services, 111 com acesso de fora", or why it failed.</summary>
    [ObservableProperty]
    public partial string? KubernetesStatus { get; set; }

    [ObservableProperty]
    public partial bool KubernetesFailed { get; set; }

    [ObservableProperty]
    public partial string? DockerStatus { get; set; }

    [ObservableProperty]
    public partial bool DockerFailed { get; set; }

    /// <summary>Node IPs: any of them answers any NodePort.</summary>
    public ObservableCollection<string> Nodes { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNodes))]
    public partial string? SelectedNode { get; set; }

    public bool HasNodes => Nodes.Count > 1;

    [ObservableProperty]
    public partial string PasteText { get; set; } = "";

    [ObservableProperty]
    public partial string PasteNode { get; set; } = "";

    [ObservableProperty]
    public partial string? PasteError { get; set; }

    public ObservableCollection<CandidateItemViewModel> Visible { get; } = [];

    public ObservableCollection<string> Namespaces { get; } = [AllNamespaces];

    [ObservableProperty]
    public partial string Search { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSelectNamespace))]
    public partial string SelectedNamespace { get; set; } = AllNamespaces;

    public bool CanSelectNamespace => SelectedNamespace != AllNamespaces;

    [ObservableProperty]
    public partial string SelectNamespaceLabel { get; set; } = "Marcar o namespace inteiro";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ImportLabel), nameof(HasCandidates))]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand))]
    public partial int SelectedCount { get; set; }

    public string ImportLabel => SelectedCount == 0 ? "Importar" : $"Importar {SelectedCount}";

    public bool HasCandidates => _all.Count > 0;

    /// <summary>"3 de 119 marcados · 2 mostrando".</summary>
    [ObservableProperty]
    public partial string ListSummary { get; set; } = "";

    /// <summary>What the import did, once it ran.</summary>
    public IReadOnlyList<PlannedService>? Result { get; private set; }

    public event EventHandler<bool>? CloseRequested;

    /// <summary>Lists the sources and asks every running one, then shows the first with something to import.</summary>
    public async Task InitializeAsync()
    {
        IsLoading = true;
        var running = await _discovery.SourcesAsync(CancellationToken.None);
        var stopped = await _discovery.StoppedSourcesAsync(CancellationToken.None);
        foreach (var source in running)
            Sources.Add(new SourceOption(source));
        foreach (var source in stopped)
            Sources.Add(new SourceOption(source, Stopped: true));
        Sources.Add(new SourceOption(null));

        var scans = await Task.WhenAll(Sources.Where(s => s is { IsPaste: false, Stopped: false })
            .Select(async s => (Option: s, Scan: await ScanAsync(s.Source!, null, UseKubernetes, UseDocker, previous: null))));
        foreach (var (option, scan) in scans)
            _scans[option] = scan;
        IsLoading = false;

        var best = scans.FirstOrDefault(s => Count(s.Scan) > 0).Option ?? Sources[0];
        SelectedSource = best;
    }

    private int Count(SourceScan scan) =>
        (UseKubernetes ? scan.Kubernetes?.Services.Count ?? 0 : 0) + (UseDocker ? scan.Docker?.Containers.Count ?? 0 : 0);

    partial void OnSelectedSourceChanged(SourceOption? value)
    {
        if (value is null || value.IsPaste)
        {
            ShowScan(null);
            return;
        }
        _scans.TryGetValue(value, out var scan);
        var kubernetes = UseKubernetes && scan?.Kubernetes is null;
        var docker = UseDocker && scan?.Docker is null;
        if (kubernetes || docker)
            _ = LoadAsync(value, null, kubernetes, docker);
        else
            ShowScan(scan);
    }

    partial void OnSelectedNodeChanged(string? value)
    {
        if (_batch || value is null)
            return;
        _nodeChoice = value;
        if (SelectedSource is not { IsPaste: false } source || !_scans.TryGetValue(source, out var scan) || scan.Kubernetes is not { } kubernetes)
            return;
        // Picking another node points every NodePort at it: ask again with its IP. The same node
        // by its DNS name only changes how the destinations are written.
        var address = _nodeNames.GetValueOrDefault(value, value);
        if (kubernetes.Node != address)
            _ = LoadAsync(source, address, kubernetes: true, docker: false);
        else
            ShowScan(scan);
    }

    [RelayCommand]
    private Task ReloadAsync() =>
        SelectedSource is { IsPaste: false } source ? LoadAsync(source, SelectedNode, UseKubernetes, UseDocker) : Task.CompletedTask;

    /// <summary>Asks the given tools of the source again; the other tool's answer, if any, stays.</summary>
    private async Task LoadAsync(SourceOption option, string? node, bool kubernetes, bool docker)
    {
        _loading?.Cancel();
        var loading = _loading = new CancellationTokenSource();
        IsLoading = true;
        try
        {
            _scans.TryGetValue(option, out var previous);
            var scan = await ScanAsync(option.Source!, node, kubernetes, docker, previous, loading.Token);
            if (loading.IsCancellationRequested)
                return;
            _scans[option] = scan;
            if (option.Stopped)
            {
                // Started by the scan: from now on it is a running distro.
                var index = Sources.IndexOf(option);
                var running = option with { Stopped = false };
                _scans[running] = scan;
                Sources[index] = running;
                SelectedSource = running;
            }
            if (SelectedSource == option || SelectedSource == option with { Stopped = false })
                ShowScan(scan);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (_loading == loading)
                IsLoading = false;
        }
    }

    private async Task<SourceScan> ScanAsync(CommandSource source, string? node, bool kubernetes, bool docker, SourceScan? previous, CancellationToken cancellationToken = default)
    {
        var kubernetesTask = kubernetes ? _discovery.KubernetesAsync(source, cancellationToken, node) : Task.FromResult(previous?.Kubernetes!);
        var dockerTask = docker ? _discovery.DockerAsync(source, cancellationToken) : Task.FromResult(previous?.Docker!);
        return new SourceScan(await kubernetesTask, await dockerTask);
    }

    private void ShowScan(SourceScan? scan)
    {
        var candidates = new List<ServiceCandidate>();
        var source = SelectedSource?.Source;

        KubernetesStatus = null;
        DockerStatus = null;
        KubernetesFailed = false;
        DockerFailed = false;

        // Nodes the hosts has a name for can be picked by that name, and it is the default: the
        // destinations then follow the name when the node's IP changes in the DNS tab.
        var shownKubernetes = UseKubernetes ? scan?.Kubernetes : null;
        _nodeNames = _destinations
            .Where(d => shownKubernetes?.Nodes.Contains(d.Address) == true)
            .GroupBy(d => d.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Address, StringComparer.Ordinal);
        var node = shownKubernetes?.Node;
        var nodeByName = _nodeChoice is not null && _nodeNames.GetValueOrDefault(_nodeChoice) == node ? _nodeChoice
            : _nodeChoice is not null && _nodeChoice == node ? null
            : _nodeNames.FirstOrDefault(n => n.Value == node).Key;

        if (shownKubernetes is { } kubernetes && source is not null)
        {
            KubernetesFailed = kubernetes.Error is not null;
            KubernetesStatus = kubernetes.Error is { } error
                ? error
                : $"{kubernetes.Context} · {Plural(kubernetes.Services.Count, "service", "services")}, {kubernetes.Services.Count(s => s.CanImport)} com acesso de fora";
            var services = nodeByName is not null && node is not null
                ? DiscoveredService.UseName(kubernetes.Services, node, nodeByName)
                : kubernetes.Services;
            candidates.AddRange(services.Select(s => new ServiceCandidate(s, new ServiceOrigin
            {
                Kind = ServiceKind.Kubernetes,
                Source = source.Id,
                Context = kubernetes.Context,
                Namespace = s.Namespace,
                Name = s.Name,
            })));
        }
        if (scan?.Docker is { } docker && source is not null && UseDocker)
        {
            DockerFailed = docker.Error is not null;
            DockerStatus = docker.Error is { } error
                ? error
                : $"{Plural(docker.Containers.Count, "container rodando", "containers rodando")}";
            candidates.AddRange(docker.Containers.Select(s => new ServiceCandidate(s, new ServiceOrigin
            {
                Kind = ServiceKind.Docker,
                Source = source.Id,
                Context = docker.Engine,
                Namespace = s.Namespace,
                Name = s.Name,
            })));
        }

        _batch = true;
        Nodes.Clear();
        foreach (var address in shownKubernetes?.Nodes ?? [])
            Nodes.Add(address);
        foreach (var name in _nodeNames.Keys)
            Nodes.Add(name);
        SelectedNode = nodeByName ?? shownKubernetes?.Node;
        OnPropertyChanged(nameof(HasNodes));
        _batch = false;

        SetCandidates(candidates);
    }

    [RelayCommand]
    private void ReadPaste()
    {
        var found = ServiceDiscovery.FromPaste(PasteText, PasteNode);
        if (found is null)
        {
            PasteError = "Não reconheci o texto. Cole a saída de kubectl get svc -A -o json ou de docker ps --format json.";
            SetCandidates([]);
            return;
        }
        PasteError = found.Count == 0 ? "Nenhum service ou container no texto." : null;
        SetCandidates([.. found.Select(s => new ServiceCandidate(s, new ServiceOrigin
        {
            Kind = s.Source,
            Source = PastedSource,
            Namespace = s.Namespace,
            Name = s.Name,
        }))]);
    }

    /// <summary>The origin source of pasted services, which "Atualizar" cannot ask again.</summary>
    public const string PastedSource = "paste";

    private void SetCandidates(IReadOnlyList<ServiceCandidate> candidates)
    {
        // Keep what was marked when the same service shows up again (another node, a reload).
        var marked = _all.Where(i => i.IsSelected).Select(i => i.Candidate.Origin).ToList();
        _batch = true;
        _all = [.. candidates.Select(c => new CandidateItemViewModel(c, OnItemSelectionChanged)
        {
            IsSelected = c.Service.CanImport && marked.Any(m => ServiceImport.SameService(m, c.Origin)),
        })];
        _batch = false;

        var namespaces = _all.Select(i => i.Namespace).Distinct().Order(StringComparer.Ordinal).ToList();
        var current = SelectedNamespace;
        Namespaces.Clear();
        Namespaces.Add(AllNamespaces);
        foreach (var ns in namespaces)
            Namespaces.Add(ns);
        SelectedNamespace = namespaces.Contains(current) ? current : AllNamespaces;

        OnPropertyChanged(nameof(HasCandidates));
        UpdatePlan();
        ApplyFilter();
    }

    partial void OnSearchChanged(string value) => ApplyFilter();

    partial void OnSelectedNamespaceChanged(string value)
    {
        ApplyFilter();
        UpdateNamespaceLabel();
    }

    private void UpdateNamespaceLabel()
    {
        var items = _all.Where(i => i.Namespace == SelectedNamespace && i.CanImport).ToList();
        SelectNamespaceLabel = items.Count > 0 && items.All(i => i.IsSelected) ? "Desmarcar o namespace" : "Marcar o namespace inteiro";
    }

    private void ApplyFilter()
    {
        var search = Search.Trim();
        Visible.Clear();
        foreach (var item in _all)
        {
            if (SelectedNamespace != AllNamespaces && item.Namespace != SelectedNamespace)
                continue;
            if (search.Length > 0 && !item.Matches(search))
                continue;
            Visible.Add(item);
        }
        UpdateSummary();
    }

    /// <summary>Marks every importable service of the namespace, or clears them when all are marked.</summary>
    [RelayCommand]
    private void SelectNamespace()
    {
        var items = _all.Where(i => i.Namespace == SelectedNamespace && i.CanImport).ToList();
        var mark = !items.All(i => i.IsSelected);
        _batch = true;
        foreach (var item in items)
            item.IsSelected = mark;
        _batch = false;
        UpdatePlan();
    }

    private void OnItemSelectionChanged()
    {
        if (!_batch)
            UpdatePlan();
    }

    /// <summary>Warnings for every row: marked ones as a batch, in list order; the rest on their own.</summary>
    private void UpdatePlan()
    {
        var selected = _all.Where(i => i.IsSelected).ToList();
        var plan = _services.Plan([.. selected.Select(i => i.Candidate)]);
        for (var i = 0; i < selected.Count; i++)
            selected[i].Apply(plan[i]);
        foreach (var item in _all.Where(i => !i.IsSelected && i.CanImport))
            item.Apply(_services.Plan([item.Candidate])[0]);

        SelectedCount = plan.Count(p => p.Route is not null);
        UpdateNamespaceLabel();
        UpdateSummary();
    }

    private void UpdateSummary() =>
        ListSummary = _all.Count == 0 ? ""
            : $"{_all.Count(i => i.IsSelected)} de {_all.Count} marcados" + (Visible.Count < _all.Count ? $" · {Visible.Count} na lista" : "");

    [RelayCommand(CanExecute = nameof(CanImport))]
    private void Import()
    {
        Result = _services.Import([.. _all.Where(i => i.IsSelected).Select(i => i.Candidate)]);
        CloseRequested?.Invoke(this, true);
    }

    private bool CanImport() => SelectedCount > 0;

    [RelayCommand]
    private void Cancel()
    {
        _loading?.Cancel();
        CloseRequested?.Invoke(this, false);
    }

    /// <summary>"12 serviços importados · 1 atualizado · 2 ficaram de fora".</summary>
    public static string Describe(IReadOnlyList<PlannedService> result)
    {
        var added = result.Count(p => p.Route is not null && p.Replaces is null);
        var updated = result.Count(p => p.Replaces is not null);
        var failed = result.Count(p => p.Route is null);
        var parts = new List<string>();
        if (added > 0)
            parts.Add(added == 1 ? "1 serviço importado" : $"{added} serviços importados");
        if (updated > 0)
            parts.Add(updated == 1 ? "1 atualizado" : $"{updated} atualizados");
        if (failed > 0)
            parts.Add(failed == 1 ? "1 ficou de fora" : $"{failed} ficaram de fora");
        return parts.Count == 0 ? "Nada importado" : string.Join(" · ", parts);
    }

    private static string Plural(int count, string one, string many) => count == 1 ? $"1 {one}" : $"{count} {many}";
}

/// <summary>One row of the import list.</summary>
public sealed partial class CandidateItemViewModel(ServiceCandidate candidate, Action selectionChanged) : ObservableObject
{
    public ServiceCandidate Candidate { get; } = candidate;
    private DiscoveredService Service => Candidate.Service;

    public string Name => Service.Name;

    /// <summary>Namespace, Compose project, or "(sem projeto)" for a plain container.</summary>
    public string Namespace => Service.Namespace.Length > 0 ? Service.Namespace : "(sem projeto)";

    public string KindText => Service.Source == ServiceKind.Docker ? "Docker" : Service.Kind;
    public bool CanImport => Service.CanImport;
    public string Detail => CanImport ? string.Join("   ", Service.Ports) : Service.Unreachable ?? "";
    public string NamesText => string.Join(", ", Service.Names);
    public bool NoReadyPods => CanImport && Service.Ready == false;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    partial void OnIsSelectedChanged(bool value) => selectionChanged();

    /// <summary>A name clash, or why it cannot be imported.</summary>
    [ObservableProperty]
    public partial string? Warning { get; set; }

    /// <summary>Imported before from the same place: importing again updates its ports.</summary>
    [ObservableProperty]
    public partial bool IsUpdate { get; set; }

    public bool Matches(string search) =>
        Service.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
        || Service.Namespace.Contains(search, StringComparison.OrdinalIgnoreCase)
        || Service.Names.Any(n => n.Contains(search, StringComparison.OrdinalIgnoreCase));

    public void Apply(PlannedService planned)
    {
        Warning = CanImport ? planned.Warning : null;
        IsUpdate = planned.Replaces is not null;
    }
}
