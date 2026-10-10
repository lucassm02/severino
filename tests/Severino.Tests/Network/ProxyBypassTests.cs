using Severino.Core.Certificates;
using Severino.Core.Network;

namespace Severino.Tests.Network;

public sealed class ProxyBypassTests
{
    [Theory]
    [InlineData("<local>", "intranet", true)]
    [InlineData("<local>", "callfred.sev", false)] // the trap: <local> skips only names without a dot
    [InlineData("*.sev", "callfred.sev", true)]
    [InlineData("*.sev", "api.callfred.sev", true)]
    [InlineData("*.sev", "sev", false)]
    [InlineData("*.SEV", "CallFred.sev", true)]
    [InlineData("api.empresa.com", "api.empresa.com", true)]
    [InlineData("api.empresa.com", "xapi.empresa.com", false)]
    [InlineData("http://callfred.sev", "callfred.sev", true)]
    [InlineData("callfred.sev:80", "callfred.sev", true)]
    [InlineData("10.*", "callfred.sev", false)]
    public void Matches_like_wininet(string entry, string host, bool expected)
    {
        Assert.Equal(expected, ProxyBypass.Matches(entry, host));
    }

    [Fact]
    public void Only_a_fixed_proxy_that_is_on_takes_domains()
    {
        string[] domains = ["callfred.sev", "api.sev", "intranet"];

        Assert.Empty(ProxyBypass.Uncovered(new(false, "127.0.0.1:5559", null, null), domains)); // filled in but off
        Assert.Empty(ProxyBypass.Uncovered(new(true, "", null, null), domains));
        Assert.Empty(ProxyBypass.Uncovered(new(false, null, null, "http://wpad/proxy.pac"), domains)); // a script decides alone

        var taken = ProxyBypass.Uncovered(new(true, "proxy.empresa:8080", "<local>;api.sev", null), domains);
        Assert.Equal(["callfred.sev"], taken);
    }

    [Fact]
    public void Entries_cover_a_whole_tld_that_does_not_exist()
    {
        var entries = ProxyBypass.EntriesFor(["callfred.sev", "api.sev", "api.empresa.com"], tld => tld == "com");

        Assert.Equal(["*.sev", "api.empresa.com"], entries.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Add_and_remove_touch_only_the_given_entries()
    {
        var company = "<local>;*.empresa.local;10.*";

        var added = ProxyBypass.Add(company, ["*.sev", "*.EMPRESA.local"]);
        Assert.Equal("<local>;*.empresa.local;10.*;*.sev", added);

        Assert.Equal(company, ProxyBypass.Remove(added, ["*.SEV"]));
        Assert.Equal("*.sev", ProxyBypass.Add(null, ["*.sev"]));
        Assert.Equal("", ProxyBypass.Remove("*.sev", ["*.sev"]));
    }

    [Fact]
    public void Firefox_trusts_windows_roots_by_preference_or_version()
    {
        const string on = """user_pref("security.enterprise_roots.enabled", true);""";
        const string off = """user_pref("security.enterprise_roots.enabled", false);""";

        Assert.True(FirefoxTrust.TrustsWindowsRoots(on, "LastVersion=115.0_20230101/20230101"));
        Assert.False(FirefoxTrust.TrustsWindowsRoots(off, "LastVersion=128.0_20240701/20240701"));
        Assert.True(FirefoxTrust.TrustsWindowsRoots("", "[Compatibility]\nLastVersion=128.0_20240701/20240701"));
        Assert.False(FirefoxTrust.TrustsWindowsRoots("", "[Compatibility]\nLastVersion=115.0_20230101/20230101"));
        Assert.True(FirefoxTrust.TrustsWindowsRoots("", null));
    }

    [Fact]
    public void Firefox_profiles_that_would_refuse_the_ca_are_named()
    {
        var root = Path.Combine(Path.GetTempPath(), "severino-tests", Guid.NewGuid().ToString("N"));
        try
        {
            void Profile(string folder, string prefs)
            {
                Directory.CreateDirectory(Path.Combine(root, folder));
                File.WriteAllText(Path.Combine(root, folder, "prefs.js"), prefs);
            }
            Profile("a1b2.default-release", """user_pref("security.enterprise_roots.enabled", false);""");
            Profile("c3d4.trabalho", "");
            Directory.CreateDirectory(Path.Combine(root, "e5f6.nunca-aberto")); // no prefs.js yet

            Assert.Equal(["default-release"], FirefoxTrust.ProfilesIgnoringWindowsRoots(root));
            Assert.Empty(FirefoxTrust.ProfilesIgnoringWindowsRoots(Path.Combine(root, "nao-existe")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
