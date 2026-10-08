using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Decisya.Bff.Session;
using Decisya.ServiceDefaults.Logging;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Decisya.Bff.Tests;

/// <summary>
/// Issue #121, G2 D3 and D4, G3 G4-121-04 (b), (c), (d): the BFF's <c>acr_values=2</c> on every
/// authorize request and its authentication events, driven without Docker. The OIDC handler runs
/// against a static configuration and a fake back channel (a discovery document is never fetched; the
/// token endpoint, and the PAR endpoint when advertised, answer from the fake). Every failure branch
/// ends in the generic 500 and never rethrows into the exception handler.
/// </summary>
public class BffAuthEventsTests
{
    private const string Authority = "https://idp.example/realms/decisya";
    private const string AuthorizationEndpoint = Authority + "/protocol/openid-connect/auth";
    private const string TokenEndpoint = Authority + "/protocol/openid-connect/token";
    private const string ParEndpoint = Authority + "/protocol/openid-connect/ext/par/request";

    // ------------------------------------------------------------------ acr_values

    [Fact]
    public async Task Every_authorize_request_carries_acr_values_2_exactly_once()
    {
        var provider = new CapturingLoggerProvider();
        using var factory = EventsFactory.Create(provider);
        using var client = factory.CreateBffClient(new OriginCookieJar());

        using var response = await client.GetAsync("/bff/login?returnUrl=%2Fdashboard", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Found, "captured Warning and Error records: " + string.Join(" || ", provider.Records.Where(r => r.Level >= LogLevel.Warning).Select(r => r.Category + ": " + r.Message + " :: " + r.ExceptionText)));
        var query = ParseQuery(response.Headers.Location!);
        response.Headers.Location!.GetLeftPart(UriPartial.Path).Should().Be(AuthorizationEndpoint);
        query.GetValues("acr_values").Should().Equal("2");
        query["response_type"].Should().Be("code");
        query["code_challenge_method"].Should().Be("S256");
    }

    [Fact]
    public async Task With_pushed_authorization_the_pushed_request_carries_acr_values_2()
    {
        var par = new FakeIdentityProvider { PushedAuthorization = true };
        using var factory = EventsFactory.Create(new CapturingLoggerProvider(), par);
        using var client = factory.CreateBffClient(new OriginCookieJar());

        using var response = await client.GetAsync("/bff/login", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        par.Requests.Should().ContainSingle(r => r.Uri == ParEndpoint);
        System.Web.HttpUtility.ParseQueryString(par.Requests.Single(r => r.Uri == ParEndpoint).Body)
            .GetValues("acr_values").Should().Equal("2");
    }

    [Fact]
    public void The_sign_out_redirect_is_not_an_authorize_request_and_carries_no_acr_values()
    {
        var message = new OpenIdConnectMessage { RequestType = OpenIdConnectRequestType.Logout };
        var context = new RedirectContext(
            new DefaultHttpContext(), new AuthenticationScheme("oidc", "oidc", typeof(OpenIdConnectHandler)),
            new OpenIdConnectOptions(), new AuthenticationProperties()) { ProtocolMessage = message };

        AuthEvents.OnRedirectToIdentityProvider(context);

        message.AcrValues.Should().BeNullOrEmpty();
    }

    // ------------------------------------------------------------------ failure events on the real callback

    [Fact]
    public async Task An_access_denied_callback_logs_one_warning_and_ends_in_the_generic_500_with_nothing_rethrown()
    {
        var provider = new CapturingLoggerProvider();
        using var factory = EventsFactory.Create(provider);
        var jar = new OriginCookieJar();
        using var client = factory.CreateBffClient(jar);
        var state = await StartLoginAsync(client);
        var canary = Canaries.Unique("description");

        using var response = await client.GetAsync(
            $"/signin-oidc?error=access_denied&error_description={canary}&state={Uri.EscapeDataString(state)}", TestContext.Current.CancellationToken);

        await AssertGenericFailureAsync(response, provider, SignInFailureReason.AccessDenied, canary);
    }

    [Fact]
    public async Task A_callback_with_an_unreadable_state_logs_state_invalid()
    {
        var provider = new CapturingLoggerProvider();
        using var factory = EventsFactory.Create(provider);
        using var client = factory.CreateBffClient(new OriginCookieJar());
        var canary = Canaries.Unique("state");

        using var response = await client.GetAsync(
            $"/signin-oidc?code={canary}&state={canary}", TestContext.Current.CancellationToken);

        await AssertGenericFailureAsync(response, provider, SignInFailureReason.StateInvalid, canary);
    }

    [Fact]
    public async Task A_callback_without_the_correlation_cookie_logs_state_invalid()
    {
        var provider = new CapturingLoggerProvider();
        using var factory = EventsFactory.Create(provider);
        using var starter = factory.CreateBffClient(new OriginCookieJar());
        var state = await StartLoginAsync(starter);
        using var cookieless = factory.CreateBffClient(new OriginCookieJar());

        using var response = await cookieless.GetAsync(
            $"/signin-oidc?code=abc&state={Uri.EscapeDataString(state)}", TestContext.Current.CancellationToken);

        await AssertGenericFailureAsync(response, provider, SignInFailureReason.StateInvalid);
    }

    [Fact]
    public async Task A_provider_error_that_is_not_access_denied_logs_remote_failure_and_never_the_description()
    {
        var provider = new CapturingLoggerProvider();
        using var factory = EventsFactory.Create(provider);
        using var client = factory.CreateBffClient(new OriginCookieJar());
        var state = await StartLoginAsync(client);
        var canary = Canaries.Unique("description");

        using var response = await client.GetAsync(
            $"/signin-oidc?error=server_error&error_description={canary}&state={Uri.EscapeDataString(state)}",
            TestContext.Current.CancellationToken);

        await AssertGenericFailureAsync(response, provider, SignInFailureReason.RemoteFailure, canary);
    }

    [Fact]
    public async Task A_token_endpoint_error_logs_token_exchange_failed_and_never_the_provider_text()
    {
        var provider = new CapturingLoggerProvider();
        var canary = Canaries.Unique("provider-text");
        var idp = new FakeIdentityProvider { TokenResponse = (HttpStatusCode.BadRequest, $$"""{"error":"invalid_grant","error_description":"{{canary}}"}""") };
        using var factory = EventsFactory.Create(provider, idp);
        using var client = factory.CreateBffClient(new OriginCookieJar());
        var state = await StartLoginAsync(client);

        using var response = await client.GetAsync(
            $"/signin-oidc?code={Canaries.Unique("code")}&state={Uri.EscapeDataString(state)}", TestContext.Current.CancellationToken);

        await AssertGenericFailureAsync(response, provider, SignInFailureReason.TokenExchangeFailed, canary);
        idp.Requests.Should().Contain(r => r.Uri == TokenEndpoint);
    }

    [Fact]
    public async Task An_unreachable_token_endpoint_logs_remote_failure()
    {
        var provider = new CapturingLoggerProvider();
        var idp = new FakeIdentityProvider { ThrowOnToken = true };
        using var factory = EventsFactory.Create(provider, idp);
        using var client = factory.CreateBffClient(new OriginCookieJar());
        var state = await StartLoginAsync(client);

        using var response = await client.GetAsync(
            $"/signin-oidc?code=abc&state={Uri.EscapeDataString(state)}", TestContext.Current.CancellationToken);

        await AssertGenericFailureAsync(response, provider, SignInFailureReason.RemoteFailure);
    }

    /// <summary>G4-121-04 (c): the OIDC handler logs the provider's <c>error_description</c> and the redeem
    /// exception text at Error, which passes the <c>Microsoft.AspNetCore: Warning</c> threshold. The category is
    /// pinned to None in appsettings.json; the failure tests above run with that real threshold and assert no
    /// framework Error record exists, so a removed override fails them with the leaked text in the message.</summary>
    [Fact]
    public void The_OpenIdConnect_handler_category_is_pinned_to_None_in_the_shipped_configuration()
    {
        using var factory = EventsFactory.Create(new CapturingLoggerProvider());

        factory.Services.GetRequiredService<IConfiguration>()["Logging:LogLevel:Microsoft.AspNetCore.Authentication.OpenIdConnect"]
            .Should().Be("None");
    }

    // ------------------------------------------------------------------ the classifier and the handlers

    [Fact]
    public void The_failure_reasons_are_a_closed_list_and_classification_uses_the_exception_type_only()
    {
        SignInFailureReason.All.Should().BeEquivalentTo(
            ["access_denied", "state_invalid", "token_exchange_failed", "remote_failure", "ticket_store_unavailable", "other"]);

        var canary = Canaries.Unique("message");
        var cases = new (Exception? Failure, bool Code, string Expected)[]
        {
            (new AuthenticationFailureException(canary), false, "state_invalid"),
            (new HttpRequestException(canary), true, "remote_failure"),
            (new TaskCanceledException(canary), true, "remote_failure"),
            (new TimeoutException(canary), false, "remote_failure"),
            (new OpenIdConnectProtocolException(canary), true, "token_exchange_failed"),
            (new OpenIdConnectProtocolException(canary), false, "remote_failure"),
            (new SecurityTokenException(canary), true, "token_exchange_failed"),
            (new SecurityTokenSignatureKeyNotFoundException(canary), true, "token_exchange_failed"),
            (new AggregateException(new HttpRequestException(canary)), false, "remote_failure"),
            (new InvalidOperationException(canary), true, "other"),
            (null, false, "other"),
        };

        foreach (var (failure, code, expected) in cases)
        {
            SignInFailureReason.Classify(failure, code).Should().Be(expected, failure?.GetType().Name ?? "null");
        }
    }

    [Theory]
    [InlineData("1", "1")]
    [InlineData("2", "2")]
    [InlineData("3", "other")]
    [InlineData("", "other")]
    [InlineData(null, "other")]
    public void The_acr_field_is_one_of_1_2_or_other(string? claim, string expected)
    {
        var identity = new ClaimsIdentity(claim is null ? [] : [new Claim("acr", claim)], "test");

        AuthEvents.ClassifyAcr(new ClaimsPrincipal(identity)).Should().Be(expected);
        AuthEvents.ClassifyAcr(null).Should().Be("other");
    }

    [Fact]
    public async Task Both_failure_handlers_call_HandleResponse_and_write_the_generic_500_themselves()
    {
        using var factory = EventsFactory.Create(new CapturingLoggerProvider());
        var options = factory.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(OpenIdConnectDefaults.AuthenticationScheme);
        var scheme = new AuthenticationScheme(OpenIdConnectDefaults.AuthenticationScheme, "oidc", typeof(OpenIdConnectHandler));

        var denied = new AccessDeniedContext(NewHttpContext(factory), scheme, options);
        await options.Events.AccessDenied(denied);
        denied.Result.Should().NotBeNull();
        denied.Result!.Handled.Should().BeTrue("OnAccessDenied must handle the response so nothing rethrows");
        denied.HttpContext.Response.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);

        foreach (var failure in new Exception[] { new AuthenticationFailureException("x"), new HttpRequestException("x"), new InvalidOperationException("x"), new OpenIdConnectProtocolException("x") })
        {
            var remote = new RemoteFailureContext(NewHttpContext(factory), scheme, options, failure);
            await options.Events.RemoteFailure(remote);
            remote.Result.Should().NotBeNull(failure.GetType().Name);
            remote.Result!.Handled.Should().BeTrue($"OnRemoteFailure must handle the response for {failure.GetType().Name}");
            remote.HttpContext.Response.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
            remote.HttpContext.Response.Headers.CacheControl.ToString().Should().Contain("no-store");
        }
    }

    // ------------------------------------------------------------------ auth.signin.succeeded and the enrichment scope

    [Fact]
    public async Task OnSignedIn_logs_one_Information_event_with_the_hashed_user_and_the_tenant_and_restores_the_scope()
    {
        var provider = new CapturingLoggerProvider();
        using var factory = EventsFactory.Create(provider);
        var enrichment = factory.Services.GetRequiredService<ILogEnrichmentContext>();
        provider.Enrichment = enrichment;
        var sub = Canaries.Unique("sub");
        var tenant = Guid.NewGuid().ToString("D");
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", sub), new Claim("tenant_id", tenant), new Claim("acr", "2")], "test"));

        using (enrichment.Begin("outer-tenant", "outer-user"))
        {
            var outer = enrichment.UserIdHash;
            await AuthEvents.OnSignedIn(NewSignedInContext(factory, principal));

            enrichment.UserIdHash.Should().Be(outer, "the one-off scope is restored in finally, never left stale");
            enrichment.TenantId.Should().Be("outer-tenant");
        }

        enrichment.UserIdHash.Should().BeNull();
        enrichment.TenantId.Should().BeNull();

        var events = provider.Records.Where(r => r.EventName == "auth.signin.succeeded").ToList();
        events.Should().ContainSingle();
        events[0].Level.Should().Be(LogLevel.Information);
        events[0].StateText.Should().Contain("Acr=2");
        events[0].TenantId.Should().Be(tenant);
        events[0].UserIdHash.Should().NotBeNullOrEmpty().And.NotContain(sub);
        provider.Records.Should().NotContain(r => r.Contains(sub), "the raw sub is never logged");
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("{7c9e6679-7425-40de-944b-e07fc1f90ae7}")]
    public async Task A_tenant_claim_that_is_not_a_canonical_non_empty_guid_is_not_put_on_the_event(string tenantClaim)
    {
        var provider = new CapturingLoggerProvider();
        using var factory = EventsFactory.Create(provider);
        provider.Enrichment = factory.Services.GetRequiredService<ILogEnrichmentContext>();
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "dev-alice"), new Claim("tenant_id", tenantClaim)], "test"));

        await AuthEvents.OnSignedIn(NewSignedInContext(factory, principal));

        var record = provider.Records.Single(r => r.EventName == "auth.signin.succeeded");
        record.TenantId.Should().BeNull();
        record.Contains(tenantClaim).Should().BeFalse();
    }

    [Fact]
    public void The_event_names_levels_and_templates_are_fixed()
    {
        var provider = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(provider));
        var logger = loggerFactory.CreateLogger("test");

        AuthEventsLog.SignInSucceeded(logger, "1");
        AuthEventsLog.SignInFailed(logger, SignInFailureReason.AccessDenied);
        AuthEventsLog.SignOut(logger, "user");

        provider.Records.Select(r => (r.EventName, r.Level)).Should().Equal(
            ("auth.signin.succeeded", LogLevel.Information),
            ("auth.signin.failed", LogLevel.Warning),
            ("auth.signout", LogLevel.Information));
        provider.Records.Should().OnlyContain(r => r.ExceptionText == null);
    }

    // ------------------------------------------------------------------ helpers

    private static async Task AssertGenericFailureAsync(
        HttpResponseMessage response, CapturingLoggerProvider provider, string reason, string? canary = null)
    {
        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("status").GetInt32().Should().Be(500);
        document.RootElement.EnumerateObject().Select(p => p.Name).Should().NotContain(["detail", "exception", "error_description"]);
        if (canary is not null)
        {
            body.Should().NotContain(canary);
        }

        var events = provider.Records.Where(r => r.EventName == "auth.signin.failed").ToList();
        events.Should().ContainSingle();
        events[0].Level.Should().Be(LogLevel.Warning);
        events[0].StateText.Should().Contain($"Reason={reason}");

        // HandleResponse() means nothing rethrows into the exception handler, which would log the failure text at Error.
        // The StackExchange.Redis client logs its own Error when the (deliberately unreachable) ticket-store address
        // refuses a connection; that is the test host's setup, not a sign-in failure branch.
        var errors = provider.Records
            .Where(r => r.Level >= LogLevel.Error && r.Category != "StackExchange.Redis.ConnectionMultiplexer")
            .ToList();
        errors.Should().BeEmpty(
            "every failure branch handles the response itself: " + string.Join("; ", errors.Select(r => r.Category + ": " + r.Message + " :: " + r.ExceptionText)));
        provider.Records.Should().NotContain(r => r.Category.Contains("ExceptionHandler", StringComparison.Ordinal));
        if (canary is not null)
        {
            provider.Records.Should().NotContain(r => r.Contains(canary), "the provider's text never reaches any log record");
        }
    }

    private static async Task<string> StartLoginAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/bff/login", TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.Found);
        return ParseQuery(response.Headers.Location!)["state"]!;
    }

    private static System.Collections.Specialized.NameValueCollection ParseQuery(Uri uri) =>
        System.Web.HttpUtility.ParseQueryString(uri.Query);

    private static DefaultHttpContext NewHttpContext(EventsFactory factory) =>
        new() { RequestServices = factory.Services, Response = { Body = new MemoryStream() } };

    private static CookieSignedInContext NewSignedInContext(EventsFactory factory, ClaimsPrincipal principal)
    {
        var options = factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(CookieAuthenticationDefaults.AuthenticationScheme);
        return new CookieSignedInContext(
            NewHttpContext(factory),
            new AuthenticationScheme(CookieAuthenticationDefaults.AuthenticationScheme, "cookie", typeof(CookieAuthenticationHandler)),
            principal,
            new AuthenticationProperties(),
            options);
    }

    /// <summary>A fake identity provider: records every back-channel request and answers the token and PAR endpoints.</summary>
    private sealed class FakeIdentityProvider : HttpMessageHandler
    {
        private readonly List<(string Uri, string Body)> _requests = [];

        public bool PushedAuthorization { get; init; }

        public bool ThrowOnToken { get; init; }

        public (HttpStatusCode Status, string Body) TokenResponse { get; init; } = (HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""");

        public IReadOnlyList<(string Uri, string Body)> Requests
        {
            get
            {
                lock (_requests)
                {
                    return [.. _requests];
                }
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            var uri = request.RequestUri!.GetLeftPart(UriPartial.Path);
            lock (_requests)
            {
                _requests.Add((uri, body));
            }

            if (uri == ParEndpoint)
            {
                return new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent("""{"request_uri":"urn:ietf:params:oauth:request_uri:test","expires_in":60}""", Encoding.UTF8, "application/json"),
                };
            }

            if (uri == TokenEndpoint)
            {
                if (ThrowOnToken)
                {
                    throw new HttpRequestException("the fake identity provider is unreachable");
                }

                return new HttpResponseMessage(TokenResponse.Status)
                {
                    Content = new StringContent(TokenResponse.Body, Encoding.UTF8, "application/json"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    /// <summary>The BFF host with a static OIDC configuration and the fake back channel; every category at Debug except the configured Microsoft.AspNetCore threshold.</summary>
    private sealed class EventsFactory(CapturingLoggerProvider provider, FakeIdentityProvider? identityProvider = null) : WebApplicationFactory<Program>
    {
        private readonly FakeIdentityProvider _idp = identityProvider ?? new FakeIdentityProvider();

        /// <summary>Program.cs captures <c>ConnectionStrings:redis</c> eagerly, so it must reach the host through
        /// the environment variable path (<see cref="EagerConfigurationGuard"/>); nothing connects to this address.</summary>
        public static EventsFactory Create(CapturingLoggerProvider provider, FakeIdentityProvider? identityProvider = null) =>
            EagerConfigurationGuard.BuildWithRedisConnectionString(
                "localhost:6379", () => new EventsFactory(provider, identityProvider));

        public HttpClient CreateBffClient(OriginCookieJar jar) =>
            new(new OriginCookieHandler(jar, Server.CreateHandler())) { BaseAddress = new Uri("https://localhost:7200") };

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                var overrides = TestConfiguration.GoodOverrides();
                overrides["Bff:Oidc:Authority"] = Authority;
                overrides["Bff:DataProtection:KeyRingPath"] = BffFactoryFactory.CreateTemporaryKeyRingDirectory();
                // The real appsettings.json thresholds stay (Microsoft.AspNetCore: Warning); everything else is forced to Debug.
                overrides["Logging:LogLevel:Default"] = "Debug";
                configuration.AddInMemoryCollection(overrides);
            });

            builder.ConfigureLogging(logging => logging.AddProvider(provider));

            builder.ConfigureServices(services =>
            {
                services.Configure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, options =>
                {
                    var configuration = new OpenIdConnectConfiguration
                    {
                        Issuer = Authority,
                        AuthorizationEndpoint = AuthorizationEndpoint,
                        TokenEndpoint = TokenEndpoint,
                    };
                    if (_idp.PushedAuthorization)
                    {
                        configuration.PushedAuthorizationRequestEndpoint = ParEndpoint;
                    }

                    options.Configuration = configuration;
                    options.BackchannelHttpHandler = _idp;
                });
            });
        }
    }
}
