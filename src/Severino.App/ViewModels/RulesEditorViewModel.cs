using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Severino.Core.Configuration;
using Severino.Core.Routes;

namespace Severino.App.ViewModels;

/// <summary>The Strings.Rules_Title section, shared by the route and the service editors (RulesForm).</summary>
public sealed partial class RulesEditorViewModel : ObservableObject
{
    public RulesEditorViewModel(TrafficRules rules)
    {
        Cors = rules.Cors;
        foreach (var header in rules.RequestHeaders)
            RequestHeaders.Add(new HeaderRuleRow { Name = header.Name, Value = header.Value });
        foreach (var header in rules.ResponseHeaders)
            ResponseHeaders.Add(new HeaderRuleRow { Name = header.Name, Value = header.Value });
        DelayText = rules.DelayMs > 0 ? rules.DelayMs.ToString(CultureInfo.InvariantCulture) : "";
        FailStatus = RouteRules.FailStatuses.Contains(rules.FailStatus) ? rules.FailStatus : 503;
        FailPercentText = rules.FailPercent > 0 ? rules.FailPercent.ToString(CultureInfo.InvariantCulture) : "";
    }

    [ObservableProperty]
    public partial bool Cors { get; set; }

    public ObservableCollection<HeaderRuleRow> RequestHeaders { get; } = [];

    public ObservableCollection<HeaderRuleRow> ResponseHeaders { get; } = [];

    /// <summary>Milliseconds; empty for none.</summary>
    [ObservableProperty]
    public partial string DelayText { get; set; } = "";

    public IReadOnlyList<int> FailStatuses => RouteRules.FailStatuses;

    [ObservableProperty]
    public partial int FailStatus { get; set; } = 503;

    /// <summary>Requests in a hundred that fail on purpose; empty for none.</summary>
    [ObservableProperty]
    public partial string FailPercentText { get; set; } = "";

    [ObservableProperty]
    public partial string? Error { get; set; }

    [RelayCommand]
    private void AddRequestHeader() => RequestHeaders.Add(new HeaderRuleRow());

    [RelayCommand]
    private void AddResponseHeader() => ResponseHeaders.Add(new HeaderRuleRow());

    [RelayCommand]
    private void RemoveHeader(HeaderRuleRow? row)
    {
        if (row is not null && !RequestHeaders.Remove(row))
            ResponseHeaders.Remove(row);
    }

    public TrafficRules Build() => new()
    {
        Cors = Cors,
        RequestHeaders = Headers(RequestHeaders),
        ResponseHeaders = Headers(ResponseHeaders),
        DelayMs = Number(DelayText),
        FailStatus = FailStatus,
        FailPercent = Number(FailPercentText),
    };

    /// <summary>Rows left blank are not rules.</summary>
    private static IReadOnlyList<HeaderRule> Headers(IEnumerable<HeaderRuleRow> rows) =>
        [.. rows.Where(r => !string.IsNullOrWhiteSpace(r.Name) || !string.IsNullOrEmpty(r.Value))
            .Select(r => new HeaderRule { Name = (r.Name ?? "").Trim(), Value = r.Value ?? "" })];

    /// <summary>Empty is none; anything that is not a number is -1, which the rules refuse with a reason.</summary>
    private static int Number(string? text) =>
        string.IsNullOrWhiteSpace(text) ? 0 : int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : -1;
}
