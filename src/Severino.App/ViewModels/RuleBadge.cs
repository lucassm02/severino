using Severino.App.Resources;
using Severino.Core.Configuration;

namespace Severino.App.ViewModels;

/// <summary>A route rule on the route's card: "CORS", "+2 cabeçalhos", "atraso 800 ms", "falha 20%".</summary>
/// <param name="Caution">Changes what the browser gets: shown in the caution colour.</param>
public sealed record RuleBadge(string Text, bool Caution)
{
    public static IReadOnlyList<RuleBadge> For(TrafficRules rules)
    {
        var badges = new List<RuleBadge>();
        if (rules.Cors)
            badges.Add(new("CORS", false));
        var headers = rules.RequestHeaders.Count + rules.ResponseHeaders.Count;
        if (headers > 0)
            badges.Add(new(headers == 1 ? Strings.Rules_BadgeHeadersOne : string.Format(Strings.Rules_BadgeHeadersMany, headers), false));
        if (rules.DelayMs > 0)
            badges.Add(new(string.Format(Strings.Rules_BadgeDelay, rules.DelayMs), true));
        if (rules.FailPercent > 0)
            badges.Add(new(string.Format(Strings.Rules_BadgeFail, rules.FailPercent, rules.FailStatus), true));
        return badges;
    }
}