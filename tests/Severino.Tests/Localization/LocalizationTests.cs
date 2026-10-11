using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Severino.App.Services;
using Severino.Core.Configuration;
using AppStrings = Severino.App.Resources.Strings;
using ContractsStrings = Severino.Contracts.Resources.Strings;
using CoreStrings = Severino.Core.Resources.Strings;
using ProxyStrings = Severino.Proxy.Resources.Strings;

namespace Severino.Tests.Localization;

public sealed partial class LocalizationTests
{
    public static TheoryData<string> Projects => ["Severino.App", "Severino.Core", "Severino.Contracts", "Severino.Proxy"];

    [Theory]
    [MemberData(nameof(Projects))]
    public void Both_languages_have_the_same_keys_and_placeholders(string project)
    {
        var english = Read(project, "Strings.resx");
        var portuguese = Read(project, "Strings.pt-BR.resx");

        Assert.Equal(english.Keys.Order(), portuguese.Keys.Order());
        foreach (var (key, text) in english)
            Assert.True(Placeholders(text).SetEquals(Placeholders(portuguese[key])), $"{project} {key}: placeholders differ");
    }

    [Theory]
    [MemberData(nameof(Projects))]
    public void Every_text_is_filled_in(string project)
    {
        foreach (var file in new[] { "Strings.resx", "Strings.pt-BR.resx" })
            Assert.All(Read(project, file), entry => Assert.False(string.IsNullOrWhiteSpace(entry.Value), $"{project} {file}: {entry.Key} is empty"));
    }

    /// <summary>
    /// Text with a Portuguese look outside the resources: a word or an accent in a C# string or a
    /// XAML attribute. Comments, logs and the hosts file markers do not count.
    /// </summary>
    [Fact]
    public void No_Portuguese_left_in_the_code()
    {
        var found = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.*", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                continue;
            var xaml = file.EndsWith(".xaml", StringComparison.Ordinal);
            if (!xaml && !file.EndsWith(".cs", StringComparison.Ordinal))
                continue;
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith("<!--", StringComparison.Ordinal) || line.StartsWith('*') || line.Contains("Log"))
                    continue;
                var texts = xaml ? XamlValue().Matches(line).Select(m => m.Groups[1].Value) : CsString().Matches(line).Select(m => m.Groups[1].Value);
                foreach (var text in texts.Where(t => LooksPortuguese().IsMatch(t) && !Allowed.Any(t.Contains)))
                    found.Add($"{Path.GetRelativePath(RepoRoot(), file)}:{i + 1}: {text}");
            }
        }
        Assert.True(found.Count == 0, "Move these to Strings.resx:\n" + string.Join("\n", found));
    }

    // The name of each language in itself, and the comments Severino writes into the hosts file.
    private static readonly string[] Allowed = ["Português (Brasil)", "nao foi criada pelo Severino"];

    [Fact]
    public void English_is_the_text_built_in()
    {
        var english = CultureInfo.GetCultureInfo("en");
        Assert.Equal("Routes", AppStrings.ResourceManager.GetString(nameof(AppStrings.Tab_Routes), english));
        Assert.Equal("Give at least one name.", CoreStrings.ResourceManager.GetString(nameof(CoreStrings.Rules_NameRequired), english));
        Assert.Equal("Give a domain.", ContractsStrings.ResourceManager.GetString(nameof(ContractsStrings.Domain_Required), english));
        Assert.Equal("Route not found", ProxyStrings.ResourceManager.GetString(nameof(ProxyStrings.Error_NotFoundTitle), english));
        // A culture without a translation of its own falls back to English.
        Assert.Equal("Routes", AppStrings.ResourceManager.GetString(nameof(AppStrings.Tab_Routes), CultureInfo.GetCultureInfo("de-DE")));
    }

    [Fact]
    public void Portuguese_comes_from_the_satellite()
    {
        var portuguese = CultureInfo.GetCultureInfo("pt-BR");
        Assert.Equal("Rotas", AppStrings.ResourceManager.GetString(nameof(AppStrings.Tab_Routes), portuguese));
        Assert.Equal("Rota não encontrada", ProxyStrings.ResourceManager.GetString(nameof(ProxyStrings.Error_NotFoundTitle), portuguese));
    }

    [Theory]
    [InlineData(AppLanguage.Auto, "pt-BR", "pt-BR")]
    [InlineData(AppLanguage.Auto, "pt-PT", "pt-BR")]
    [InlineData(AppLanguage.Auto, "en-US", "en")]
    [InlineData(AppLanguage.Auto, "de-DE", "en")]
    [InlineData(AppLanguage.English, "pt-BR", "en")]
    [InlineData(AppLanguage.Portuguese, "en-US", "pt-BR")]
    public void Auto_follows_Windows_and_a_choice_wins(AppLanguage language, string windows, string expected) =>
        Assert.Equal(expected, UiLanguage.CultureFor(language, CultureInfo.GetCultureInfo(windows)).Name);

    [Fact]
    public void The_language_is_kept_in_the_config()
    {
        Assert.Equal(AppLanguage.Auto, new SeverinoConfig().Settings.Language);
    }

    private static Dictionary<string, string> Read(string project, string file) =>
        XDocument.Load(Path.Combine(RepoRoot(), "src", project, "Resources", file)).Root!
            .Elements("data")
            .ToDictionary(d => d.Attribute("name")!.Value, d => d.Element("value")?.Value ?? "");

    private static HashSet<string> Placeholders(string text) => [.. Placeholder().Matches(text).Select(m => m.Value)];

    [GeneratedRegex(@"\{\d+\}")]
    private static partial Regex Placeholder();

    // Visible XAML attributes; markup extensions and bindings start with "{".
    [GeneratedRegex(@"(?:Text|Content|Title|Header|ToolTip|PlaceholderText|Description|Message|AutomationProperties\.Name|ContentStringFormat)=""([^""{][^""]*)""")]
    private static partial Regex XamlValue();

    [GeneratedRegex(@"\$?@?""((?:[^""\\]|\\.)*)""")]
    private static partial Regex CsString();

    [GeneratedRegex(@"[ãõçáéíóúâêôà]|\bb(não|uma|para|sem|que|está|rota|rotas|serviço|nenhum|nenhuma|abrir|salvar|cancelar|ligar|desligar|porta|destino|copiar|remover|ativar|atualizar|importar|exportar|buscar|editar|novo|nova|sair|voltar|do|da|no|na|os|ou)\b", RegexOptions.IgnoreCase)]
    private static partial Regex LooksPortuguese();

    // From this source file, so it works whatever folder the tests were built to.
    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Severino.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Severino.slnx not found above " + source);
    }
}
