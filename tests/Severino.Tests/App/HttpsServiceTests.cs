using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Severino.App.Services;
using Severino.App.ViewModels;
using Severino.Core.Certificates;
using Severino.Core.Configuration;
using Severino.Core.Domains;
using Severino.Core.Routes;
using Severino.Proxy.Certificates;
using Severino.Tests.Certificates;

namespace Severino.Tests.App;

public sealed class HttpsServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "severino-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeTrustStore _trust = new();
    private readonly ConfigService _config;
    private readonly LocalCa _ca;
    private readonly HttpsService _https;

    public HttpsServiceTests()
    {
        _config = new ConfigService(new ConfigStore(Path.Combine(_dir, "config")));
        _config.Load();
        _ca = new LocalCa(new CaStore(Path.Combine(_dir, "ca")), _trust, TimeProvider.System, NullLogger<LocalCa>.Instance);
        _https = new HttpsService(_config, _ca, new TldDirectory(_config, new FakeDns()));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private RouteEntry AddRoute(string domain, bool https = false, bool redirect = false) =>
        new RouteService(_config).Save(new RouteEntry { Domain = domain, Target = "http://localhost:3000", Https = https, RedirectToHttps = redirect });

    private void RemoveRoute(string domain) =>
        new RouteService(_config).Remove(_config.Current.Routes.Single(r => r.Domain == domain).Id);

    private RouteEntry Route(string domain) => _config.Current.Routes.Single(r => r.Domain == domain);

    private readonly List<TrustPrompt> _prompts = [];

    private Task<bool> Accept(TrustPrompt prompt)
    {
        _prompts.Add(prompt);
        return Task.FromResult(true);
    }

    [Fact]
    public async Task Prompt_shows_what_windows_will_show_before_it_does()
    {
        AddRoute("a.sev");
        await _https.ActivateAsync(Accept);
        AddRoute("api.empresa.com", https: true);
        await _https.ReissueAsync(Accept);

        var (first, second) = (_prompts[0], _prompts[1]);
        Assert.StartsWith("Severino Local CA (", first.Name);
        Assert.Equal(["sev"], first.Names);
        Assert.False(first.ReplacesCurrent);
        Assert.True(second.ReplacesCurrent);
        Assert.Equal(["api.empresa.com", "sev"], second.Names);
        // Windows prints the SHA-1 in groups of 8.
        Assert.Equal(_trust.Roots.Single(), second.Thumbprint.Replace(" ", ""));
        Assert.Matches("^([0-9A-F]{8} ){4}[0-9A-F]{8}$", second.Thumbprint);
    }

    [Fact]
    public async Task Cancelling_the_explanation_skips_windows_warning()
    {
        AddRoute("a.sev");

        Assert.Equal(HttpsActionResult.Cancelled, await _https.ActivateAsync(_ => Task.FromResult(false)));

        Assert.Empty(_trust.Roots);
        Assert.False(_https.IsActive);
        Assert.False(Route("a.sev").Https);
    }

    [Fact]
    public async Task Activation_turns_https_on_for_every_route_without_adding_redirects()
    {
        AddRoute("a.sev");
        AddRoute("b.sev", https: true, redirect: true);

        Assert.Equal(HttpsActionResult.Done, await _https.ActivateAsync(Accept));

        Assert.True(_https.IsActive);
        Assert.Equal(["sev"], _https.Status.Names);
        Assert.True(Route("a.sev").Https);
        Assert.False(Route("a.sev").RedirectToHttps);
        Assert.True(Route("b.sev").RedirectToHttps);
    }

    [Fact]
    public async Task Declined_activation_leaves_the_routes_alone()
    {
        AddRoute("a.sev");
        _trust.Decline = true;

        Assert.Equal(HttpsActionResult.Declined, await _https.ActivateAsync(Accept));

        Assert.False(_https.IsActive);
        Assert.False(Route("a.sev").Https);
    }

    [Fact]
    public async Task Activation_needs_a_route()
    {
        Assert.Equal(HttpsActionResult.NoRoutes, await _https.ActivateAsync(Accept));
        Assert.Empty(_trust.Roots);
    }

    [Fact]
    public async Task Reissue_adds_new_domains_and_drops_removed_ones()
    {
        AddRoute("a.sev");
        await _https.ActivateAsync(Accept);
        AddRoute("api.empresa.com", https: true);
        Assert.Equal(["api.empresa.com"], _https.Uncovered());
        Assert.False(_https.Covers("api.empresa.com"));

        Assert.Equal(HttpsActionResult.Done, await _https.ReissueAsync(Accept));
        Assert.Equal(["api.empresa.com", "sev"], _https.Status.Names);
        Assert.Empty(_https.Uncovered());

        RemoveRoute("a.sev");
        await _https.ReissueAsync(Accept);
        Assert.Equal(["api.empresa.com"], _https.Status.Names);
        Assert.Single(_trust.Roots);
    }

    [Fact]
    public async Task Remove_keeps_the_route_flags_for_the_next_activation()
    {
        AddRoute("a.sev", https: true, redirect: true);
        await _https.ActivateAsync(Accept);

        Assert.True(await _https.RemoveAsync());

        Assert.False(_https.IsActive);
        Assert.Empty(_https.Uncovered());
        Assert.True(Route("a.sev").RedirectToHttps);
    }

    [Fact]
    public async Task Export_writes_the_root_without_its_key()
    {
        AddRoute("a.sev");
        await _https.ActivateAsync(Accept);
        var path = Path.Combine(_dir, "ca.pem");

        _https.ExportPem(path);

        var pem = File.ReadAllText(path);
        Assert.StartsWith("-----BEGIN CERTIFICATE-----", pem);
        Assert.DoesNotContain("PRIVATE KEY", pem);
    }

    [Fact]
    public async Task New_route_form_turns_https_and_redirect_on_while_the_ca_is_active()
    {
        Assert.False(Editor().Https);
        Assert.False(Editor().CanToggleHttps);

        AddRoute("a.sev");
        await _https.ActivateAsync(Accept);
        var form = Editor();

        Assert.True(form.Https);
        Assert.True(form.RedirectToHttps);
        Assert.True(form.CanToggleHttps);
    }

    [Fact]
    public async Task Hsts_preloaded_domain_locks_https_on()
    {
        AddRoute("a.sev");
        await _https.ActivateAsync(Accept);
        var form = Editor();
        form.Https = false;

        form.Domain = "meuapp.dev";

        Assert.True(form.Https);
        Assert.False(form.CanToggleHttps);
        Assert.NotNull(form.HttpsRequiredText);
        Assert.DoesNotContain(form.DomainWarnings, w => w.Kind == DomainWarningKind.HstsPreload);
    }

    [Fact]
    public async Task Domain_outside_the_ca_is_flagged_in_the_form()
    {
        AddRoute("a.sev");
        await _https.ActivateAsync(Accept);
        var form = Editor();

        form.Domain = "b.sev";
        Assert.Null(form.CoverageWarning);

        form.Domain = "api.empresa.com";
        Assert.Contains("fora da CA", form.CoverageWarning);

        form.Https = false;
        Assert.Null(form.CoverageWarning);
    }

    [Theory]
    [InlineData(80, null, "http://a.sev/")]
    [InlineData(8080, null, "http://a.sev:8080/")]
    [InlineData(80, 443, "https://a.sev/")]
    [InlineData(80, 8443, "https://a.sev:8443/")]
    public void Urls_leave_out_default_ports(int httpPort, int? httpsPort, string expected) =>
        Assert.Equal(expected, Browser.UrlFor("a.sev", httpPort, httpsPort));

    private RouteEditorViewModel Editor() =>
        new(new RouteService(_config), new DomainInspector(new FakeDns()), _https, new Navigation(), existing: null, isCopy: false);

    /// <summary>"sev" does not exist on the internet, "com" does; names never resolve.</summary>
    private sealed class FakeDns : IDnsResolver
    {
        public Task<DnsLookupResult> LookupAsync(string name, CancellationToken cancellationToken) =>
            Task.FromResult(new DnsLookupResult(DnsLookupOutcome.NotFound));

        public Task<bool?> TldExistsAsync(string tld, CancellationToken cancellationToken) =>
            Task.FromResult<bool?>(tld != "sev");
    }
}
