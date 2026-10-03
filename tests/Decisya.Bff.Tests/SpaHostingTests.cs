using System.Net;
using Decisya.Bff.Spa;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Decisya.Bff.Tests;

/// <summary>
/// #26 G2 D5 and D6, Stories 1 and 8: the SPA shell, the static assets and the security headers on
/// a real BFF host started over a per-test temporary web root (never <c>src/Decisya.Bff/wwwroot</c>).
/// No Docker: nothing here reaches Keycloak or Redis.
/// </summary>
public sealed class SpaHostingTests : IDisposable
{
    private const string IndexContent = "<!doctype html><html lang=\"en\"><title>Decisya</title><p>fixture-shell-7c1e</p></html>";
    private const string AssetContent = "console.log('fixture-asset-abc123');";

    private readonly string _webRoot = Path.Combine(Path.GetTempPath(), "decisya-bff-tests-webroot-" + Guid.NewGuid().ToString("N"));

    public SpaHostingTests()
    {
        Directory.CreateDirectory(Path.Combine(_webRoot, "assets"));
        File.WriteAllText(Path.Combine(_webRoot, "index.html"), IndexContent);
        File.WriteAllText(Path.Combine(_webRoot, "assets", "app-abc123.js"), AssetContent);
        File.WriteAllText(Path.Combine(_webRoot, "assets", ".hidden"), "hidden-canary-0b44");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(_webRoot)!, "decisya-outside-canary.txt"), "outside-canary-91d2");
    }

    public void Dispose()
    {
        if (Directory.Exists(_webRoot))
        {
            Directory.Delete(_webRoot, recursive: true);
        }

        File.Delete(Path.Combine(Path.GetDirectoryName(_webRoot)!, "decisya-outside-canary.txt"));
    }

    [Fact]
    public void The_reserved_prefix_set_is_exactly_the_seven_server_prefixes()
    {
        SpaHosting.ReservedPrefixes.Select(prefix => prefix.Value).Should().BeEquivalentTo(
            ["/bff", "/api", "/signin-oidc", "/signout-callback-oidc", "/health", "/alive", "/assets"]);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/transactions")]
    [InlineData("/no/such/page")]
    [InlineData("/bffx")]
    [InlineData("/apidocs")]
    public async Task Client_routes_get_the_shell_with_the_exact_security_headers_and_no_cookie(string path)
    {
        using var factory = CreateFactory(_webRoot);
        using var client = CreateClient(factory);

        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.ToString().Should().Be("text/html; charset=utf-8");
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Be(IndexContent);
        response.Headers.CacheControl!.NoCache.Should().BeTrue();
        response.Headers.CacheControl.NoStore.Should().BeFalse();
        response.Headers.Contains("Set-Cookie").Should().BeFalse("the shell never issues the antiforgery pair");
        AssertSecurityHeaders(response);
    }

    [Fact]
    public async Task Head_on_the_root_returns_the_headers_without_a_body()
    {
        using var factory = CreateFactory(_webRoot);
        using var client = CreateClient(factory);
        using var request = new HttpRequestMessage(HttpMethod.Head, "/");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().BeEmpty();
        AssertSecurityHeaders(response);
    }

    [Theory]
    [InlineData("/bff/does-not-exist")]
    [InlineData("/BFF/x")]
    [InlineData("/signin-oidc/x")]
    [InlineData("/signout-callback-oidc/x")]
    [InlineData("/health/x")]
    [InlineData("/alive/x")]
    [InlineData("/assets/x")]
    [InlineData("/assets/missing.js")]
    [InlineData("/favicon.ico")]
    public async Task Server_routes_and_unknown_files_answer_404_and_never_the_shell(string path)
    {
        using var factory = CreateFactory(_webRoot);
        using var client = CreateClient(factory);

        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().NotContain("fixture-shell-7c1e");
        AssertSecurityHeaders(response);
    }

    [Theory]
    [InlineData("POST", "/")]
    [InlineData("PUT", "/")]
    [InlineData("DELETE", "/")]
    [InlineData("POST", "/transactions")]
    [InlineData("PUT", "/transactions")]
    [InlineData("DELETE", "/transactions")]
    [InlineData("OPTIONS", "/transactions")]
    public async Task Other_verbs_never_get_the_shell(string method, string path)
    {
        using var factory = CreateFactory(_webRoot);
        using var client = CreateClient(factory);
        using var request = new HttpRequestMessage(new HttpMethod(method), path);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().BeEmpty();
    }

    [Fact]
    public async Task An_asset_is_served_immutable_with_the_security_headers()
    {
        using var factory = CreateFactory(_webRoot);
        using var client = CreateClient(factory);

        using var response = await client.GetAsync("/assets/app-abc123.js", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Be(AssetContent);
        response.Headers.CacheControl!.ToString().Should().Be("public, max-age=31536000, immutable");
        response.Headers.Contains("Set-Cookie").Should().BeFalse();
        AssertSecurityHeaders(response, expectNoStore: false);
    }

    [Theory]
    [InlineData("/assets/../appsettings.json")]
    [InlineData("/assets/%2e%2e/%2e%2e/appsettings.json")]
    [InlineData("/assets/..%2f..%2fappsettings.json")]
    [InlineData("/assets/..%5c..%5cappsettings.json")]
    [InlineData("/%2e%2e/appsettings.json")]
    [InlineData("/assets/.hidden")]
    [InlineData("/assets/%2e%2e/decisya-outside-canary.txt")]
    [InlineData("/assets/..%2fdecisya-outside-canary.txt")]
    [InlineData("/assets/C:/Windows/win.ini")]
    [InlineData("/assets/app-abc123.js::$DATA")]
    public async Task Path_traversal_and_hidden_files_return_no_file(string path)
    {
        using var factory = CreateFactory(_webRoot);
        using var client = CreateClient(factory);

        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.BadRequest);
        body.Should().NotContain("hidden-canary-0b44").And.NotContain("outside-canary-91d2")
            .And.NotContain("ConnectionStrings").And.NotContain("fixture-shell-7c1e").And.NotContain("fixture-asset-abc123");
    }

    [Fact]
    public async Task An_empty_web_root_answers_404_with_an_empty_body_and_logs_the_one_fixed_warning()
    {
        var emptyRoot = Path.Combine(Path.GetTempPath(), "decisya-bff-tests-emptyroot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(emptyRoot);
        var logs = new CapturingLoggerProvider();
        try
        {
            using var factory = CreateFactory(emptyRoot, logs);
            using var client = CreateClient(factory);

            using var response = await client.GetAsync("/", TestContext.Current.CancellationToken);

            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().BeEmpty();
            AssertSecurityHeaders(response);

            var warnings = logs.Records.Where(record => record.EventId == 1807).ToList();
            warnings.Should().ContainSingle();
            warnings[0].Level.Should().Be(LogLevel.Warning);
            warnings[0].Message.Should().NotContain(emptyRoot, "the start-up warning never carries a path");
        }
        finally
        {
            Directory.Delete(emptyRoot, recursive: true);
        }
    }

    /// <summary>A default <c>wwwroot</c> folder under the content root (the real SPA build, on Marco's
    /// machine) must never win over the configured web root: <c>WebRootFileProvider</c> is created
    /// from the default folder before the configured one applies, so product code must not use it.</summary>
    [Fact]
    public async Task The_configured_web_root_wins_over_a_default_wwwroot_folder_under_the_content_root()
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), "decisya-bff-tests-content-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(contentRoot, "wwwroot", "assets"));
        File.WriteAllText(Path.Combine(contentRoot, "wwwroot", "index.html"), "decoy-default-shell-5e2a");
        File.WriteAllText(Path.Combine(contentRoot, "wwwroot", "assets", "app-abc123.js"), "decoy-default-asset-5e2a");
        try
        {
            using var factory = CreateFactory(_webRoot, contentRoot: contentRoot);
            using var client = CreateClient(factory);

            var shell = await (await client.GetAsync("/transactions", TestContext.Current.CancellationToken))
                .Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            var asset = await (await client.GetAsync("/assets/app-abc123.js", TestContext.Current.CancellationToken))
                .Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            shell.Should().Be(IndexContent);
            asset.Should().Be(AssetContent);
        }
        finally
        {
            Directory.Delete(contentRoot, recursive: true);
        }
    }

    [Fact]
    public async Task A_built_web_root_logs_no_start_up_warning()
    {
        var logs = new CapturingLoggerProvider();
        using var factory = CreateFactory(_webRoot, logs);
        using var client = CreateClient(factory);

        using var response = await client.GetAsync("/", TestContext.Current.CancellationToken);

        logs.Records.Where(record => record.EventId == 1807).Should().BeEmpty();
    }

    [Fact]
    public async Task The_security_headers_are_on_a_bff_me_response_and_a_401_from_the_api_route()
    {
        using var factory = CreateFactory(_webRoot);
        using var client = CreateClient(factory);

        using var me = await client.GetAsync("/bff/me", TestContext.Current.CancellationToken);
        me.StatusCode.Should().Be(HttpStatusCode.OK);
        AssertSecurityHeaders(me);
        me.Headers.CacheControl!.NoStore.Should().BeTrue("/bff/me carries the email, so it is never cached");

        using var api = await client.GetAsync("/api/capabilities", TestContext.Current.CancellationToken);
        api.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        AssertSecurityHeaders(api);
    }

    [Fact]
    public async Task The_security_headers_are_overwritten_never_taken_from_upstream_and_cache_control_is_kept_if_set()
    {
        var headers = new Microsoft.AspNetCore.Http.HeaderDictionary
        {
            ["Content-Security-Policy"] = "default-src *",
            ["X-Frame-Options"] = "SAMEORIGIN",
            ["X-Content-Type-Options"] = "sniff",
            ["Referrer-Policy"] = "unsafe-url",
            ["Cross-Origin-Opener-Policy"] = "unsafe-none",
            ["Cross-Origin-Resource-Policy"] = "cross-origin",
            ["Permissions-Policy"] = "camera=*",
            ["Cache-Control"] = "private, max-age=5",
        };

        Decisya.Bff.Security.SecurityHeaders.Apply(headers);

        headers["Content-Security-Policy"].ToString().Should().Be(Decisya.Bff.Security.SecurityHeaders.ContentSecurityPolicy);
        headers["X-Frame-Options"].ToString().Should().Be("DENY");
        headers["X-Content-Type-Options"].ToString().Should().Be("nosniff");
        headers["Referrer-Policy"].ToString().Should().Be("no-referrer");
        headers["Cross-Origin-Opener-Policy"].ToString().Should().Be("same-origin");
        headers["Cross-Origin-Resource-Policy"].ToString().Should().Be("same-origin");
        headers["Permissions-Policy"].ToString().Should().Be(Decisya.Bff.Security.SecurityHeaders.PermissionsPolicy);
        headers["Cache-Control"].ToString().Should().Be("private, max-age=5");
        await Task.CompletedTask;
    }

    private static void AssertSecurityHeaders(HttpResponseMessage response, bool expectNoStore = true)
    {
        response.Headers.GetValues("Content-Security-Policy").Should().ContainSingle().Which.Should().Be(
            "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self'; font-src 'self'; connect-src 'self'; "
            + "object-src 'none'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'; frame-src 'none'; worker-src 'none'");
        response.Headers.GetValues("X-Content-Type-Options").Should().ContainSingle().Which.Should().Be("nosniff");
        response.Headers.GetValues("Referrer-Policy").Should().ContainSingle().Which.Should().Be("no-referrer");
        response.Headers.GetValues("X-Frame-Options").Should().ContainSingle().Which.Should().Be("DENY");
        response.Headers.GetValues("Cross-Origin-Opener-Policy").Should().ContainSingle().Which.Should().Be("same-origin");
        response.Headers.GetValues("Cross-Origin-Resource-Policy").Should().ContainSingle().Which.Should().Be("same-origin");
        response.Headers.GetValues("Permissions-Policy").Should().ContainSingle().Which.Should().Be(
            "accelerometer=(), camera=(), geolocation=(), gyroscope=(), magnetometer=(), microphone=(), payment=(), usb=()");
        response.Headers.CacheControl.Should().NotBeNull();
        if (expectNoStore)
        {
            // Either the default, or the shell's own more specific no-cache.
            (response.Headers.CacheControl!.NoStore || response.Headers.CacheControl.NoCache).Should().BeTrue();
        }
    }

    private static HttpClient CreateClient(WebApplicationFactory<Program> factory) =>
        new(factory.Server.CreateHandler()) { BaseAddress = new Uri("https://localhost:7200") };

    private static WebApplicationFactory<Program> CreateFactory(
        string webRoot, ILoggerProvider? loggerProvider = null, string? contentRoot = null)
    {
        // Program.cs reads ConnectionStrings:redis eagerly; see EagerConfigurationGuard. Nothing in
        // these tests touches Redis (anonymous callers only), so the address is never contacted.
        return EagerConfigurationGuard.BuildWithRedisConnectionString(
            "localhost:6379,connectTimeout=500,abortConnect=false", () => new SpaFactory(webRoot, loggerProvider, contentRoot),
            apiAddress: "https://decisya-api");
    }

    private sealed class SpaFactory(string webRoot, ILoggerProvider? loggerProvider, string? contentRoot) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            if (contentRoot is not null)
            {
                builder.UseContentRoot(contentRoot);
            }

            builder.UseSetting(WebHostDefaults.WebRootKey, webRoot);
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(TestConfiguration.GoodOverrides()));
            if (loggerProvider is not null)
            {
                builder.ConfigureServices(services => services.AddSingleton(loggerProvider));
            }
        }
    }
}
