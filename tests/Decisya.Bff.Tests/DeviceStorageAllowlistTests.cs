using System.Text.Json;
using Decisya.Bff.Security;
using Decisya.Bff.Session;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Decisya.Bff.Tests;

/// <summary>
/// #338 G2 D7 and D8, G3 M3 (BFF cross-check): the device-storage allowlist
/// (<c>src/Decisya.Web/src/legal/device-storage-allowlist.json</c>) and the cookies the BFF really
/// configures must agree in both directions. Every BFF cookie (antiforgery, XSRF, session, and the
/// OIDC correlation and nonce prefixes read from the configured <see cref="OpenIdConnectOptions"/>)
/// is allowlisted with <c>setBy: app</c>, and every <c>setBy: app</c> cookie entry is a cookie the
/// BFF configures. A missing or empty file fails (M3), so the check can never pass vacuously.
/// No Docker: the host is started in memory and nothing reaches Keycloak or Redis.
/// </summary>
[Trait("Category", "Architecture")]
public class DeviceStorageAllowlistTests
{
    private const string AllowlistRelativePath = "src/Decisya.Web/src/legal/device-storage-allowlist.json";

    [Fact]
    public void The_allowlist_file_exists_and_holds_entries()
    {
        var entries = LoadEntries();

        entries.Should().NotBeEmpty("an empty or missing allowlist must fail, never pass vacuously (M3)");
        entries.Select(entry => entry.Name).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Every_cookie_the_BFF_configures_is_on_the_allowlist_as_an_app_cookie()
    {
        var entries = LoadEntries();
        var configured = ConfiguredBffCookies();

        foreach (var cookie in configured)
        {
            entries.Should().Contain(
                entry => entry.Name == cookie.Name && entry.Match == cookie.Match && entry.Store == "cookie" && entry.SetBy == "app",
                "the BFF configures the {0} cookie '{1}' and the notice must list it", cookie.Match, cookie.Name);
        }
    }

    [Fact]
    public void Every_app_cookie_on_the_allowlist_is_a_cookie_the_BFF_configures()
    {
        var entries = LoadEntries();
        var configured = ConfiguredBffCookies();

        var appCookies = entries.Where(entry => entry.SetBy == "app").ToList();

        appCookies.Should().NotBeEmpty();
        appCookies.Should().OnlyContain(entry => entry.Store == "cookie", "the app sets no web storage entry in #338");
        var unknown = appCookies
            .Where(entry => !configured.Contains(new BffCookie(entry.Name, entry.Match)))
            .Select(entry => entry.Name)
            .ToList();
        unknown.Should().BeEmpty("an allowlisted app cookie the BFF does not configure would put a false entry in the notice");
    }

    [Fact]
    public void The_two_prefix_entries_are_exactly_the_configured_OIDC_correlation_and_nonce_names()
    {
        var entries = LoadEntries();
        var (correlation, nonce) = ReadOidcCookieNames();

        entries.Where(entry => entry.Match == "prefix").Select(entry => entry.Name)
            .Should().BeEquivalentTo([correlation, nonce]);
    }

    [Fact]
    public void The_configured_names_match_the_BFF_constants()
    {
        using var factory = CreateFactory();

        factory.Services.GetRequiredService<IOptions<AntiforgeryOptions>>().Value.Cookie.Name
            .Should().Be(AntiforgeryCookieNames.CookieToken);
        factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme).Cookie.Name
            .Should().Be(CookieOptionsSetup.SessionCookieName);
    }

    private static HashSet<BffCookie> ConfiguredBffCookies()
    {
        using var factory = CreateFactory();

        var antiforgeryName = factory.Services.GetRequiredService<IOptions<AntiforgeryOptions>>().Value.Cookie.Name;
        var sessionName = factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme).Cookie.Name;
        var (correlation, nonce) = ReadOidcCookieNames(factory);

        antiforgeryName.Should().NotBeNullOrWhiteSpace();
        sessionName.Should().NotBeNullOrWhiteSpace();
        correlation.Should().NotBeNullOrWhiteSpace();
        nonce.Should().NotBeNullOrWhiteSpace();

        return
        [
            new BffCookie(antiforgeryName!, "exact"),
            // Appended by hand in BffEndpoints.IssueXsrfCookie, so the constant is the only source.
            new BffCookie(AntiforgeryCookieNames.XsrfCookie, "exact"),
            new BffCookie(sessionName!, "exact"),
            new BffCookie(correlation!, "prefix"),
            new BffCookie(nonce!, "prefix"),
        ];
    }

    private static (string? Correlation, string? Nonce) ReadOidcCookieNames()
    {
        using var factory = CreateFactory();
        return ReadOidcCookieNames(factory);
    }

    private static (string? Correlation, string? Nonce) ReadOidcCookieNames(WebApplicationFactory<Program> factory)
    {
        var options = factory.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(OpenIdConnectDefaults.AuthenticationScheme);
        return (options.CorrelationCookie.Name, options.NonceCookie.Name);
    }

    private static List<AllowlistEntry> LoadEntries()
    {
        var path = RepoPaths.Find(AllowlistRelativePath);
        File.Exists(path).Should().BeTrue($"the allowlist must exist at {AllowlistRelativePath} (M3)");

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;

        root.ValueKind.Should().Be(JsonValueKind.Object);
        root.TryGetProperty("version", out var version).Should().BeTrue();
        version.GetInt32().Should().Be(1, "this test reads schema v1; a bump needs this test reviewed");
        root.TryGetProperty("entries", out var entriesElement).Should().BeTrue();
        entriesElement.ValueKind.Should().Be(JsonValueKind.Array);

        var entries = entriesElement.EnumerateArray().Select(element => new AllowlistEntry(
            Name: RequiredString(element, "name"),
            Match: RequiredString(element, "match"),
            Store: RequiredString(element, "store"),
            SetBy: RequiredString(element, "setBy"))).ToList();

        entries.Should().NotBeEmpty("an empty allowlist must fail (M3)");
        return entries;
    }

    private static string RequiredString(JsonElement element, string property)
    {
        element.TryGetProperty(property, out var value).Should().BeTrue($"every entry carries '{property}'");
        value.ValueKind.Should().Be(JsonValueKind.String);
        var text = value.GetString();
        text.Should().NotBeNullOrWhiteSpace();
        return text!;
    }

    // Program.cs reads ConnectionStrings:redis and Bff:Api:Address eagerly (see EagerConfigurationGuard).
    // Resolving the cookie options builds the Redis ticket store; nothing ever contacts Redis here.
    private static DevelopmentFactory CreateFactory() =>
        EagerConfigurationGuard.BuildWithRedisConnectionString(
            "localhost:6379,connectTimeout=500,abortConnect=false", () => new DevelopmentFactory(),
            apiAddress: "https://decisya-api");

    private sealed record AllowlistEntry(string Name, string Match, string Store, string SetBy);

    private sealed record BffCookie(string Name, string Match);

    private sealed class DevelopmentFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configurationBuilder) =>
                configurationBuilder.AddInMemoryCollection(TestConfiguration.GoodOverrides()));
        }
    }
}
