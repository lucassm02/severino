using System.Globalization;
using Severino.Core.Configuration;

namespace Severino.App.Services;

/// <summary>
/// Picks the UI language once, at start, before any window or text is created: the generated
/// Strings classes of every project read the UI culture of the thread they run on.
/// </summary>
public static class UiLanguage
{
    public static readonly CultureInfo English = CultureInfo.GetCultureInfo("en");
    public static readonly CultureInfo Portuguese = CultureInfo.GetCultureInfo("pt-BR");

    /// <summary>The culture for <paramref name="language"/>; Auto looks at the Windows display language.</summary>
    public static CultureInfo CultureFor(AppLanguage language, CultureInfo windows) => language switch
    {
        AppLanguage.English => English,
        AppLanguage.Portuguese => Portuguese,
        _ => windows.TwoLetterISOLanguageName == "pt" ? Portuguese : English,
    };

    /// <summary>The Windows display language, as it was before <see cref="Apply"/> changed anything.</summary>
    public static CultureInfo Windows { get; } = CultureInfo.CurrentUICulture;

    /// <summary>
    /// Sets the UI culture of this thread and of every thread to come, the thread pool included.
    /// Dates and numbers keep following the Windows regional format.
    /// </summary>
    public static CultureInfo Apply(AppLanguage language)
    {
        var culture = CultureFor(language, Windows);
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentUICulture = culture;
        return culture;
    }
}
