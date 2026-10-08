using System.Net;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using NodaTime;

namespace Decisya.Bff.Tests.RateLimiting;

/// <summary>
/// A Development BFF host for the limiter tests. No listener is opened: requests go through the in-process
/// TestServer. The OIDC configuration is static (no discovery, no network). The peer address of a request
/// is the <c>X-Test-Peer</c> header (absent means no address, the <c>unknown</c> bucket). With an
/// <c>edge</c> address the host also runs the forwarded-headers middleware the way
/// <c>UseDecisyaForwardedHeaders</c> does outside Development: <c>X-Forwarded-For</c> is honoured from that
/// one peer and from nobody else.
/// </summary>
internal sealed class RateLimitFactory(
    Dictionary<string, string?> overrides,
    IPAddress? edge = null,
    ILoggerProvider? loggerProvider = null,
    IClock? clock = null,
    string? redisConnectionString = null,
    string? apiAddress = null,
    OpenIdConnectConfiguration? oidcConfiguration = null,
    bool limiterOff = false) : WebApplicationFactory<Program>
{
    internal const string PeerHeader = "X-Test-Peer";

    internal static RateLimitFactory Create(
        Dictionary<string, string?>? overrides = null,
        IPAddress? edge = null,
        ILoggerProvider? loggerProvider = null,
        IClock? clock = null,
        string? redisConnectionString = null,
        string? apiAddress = null,
        OpenIdConnectConfiguration? oidcConfiguration = null,
        bool limiterOff = false)
    {
        var connection = redisConnectionString ?? "localhost:6379,connectTimeout=500,abortConnect=false";
        return EagerConfigurationGuard.BuildWithRedisConnectionString(
            connection,
            () => new RateLimitFactory(overrides ?? [], edge, loggerProvider, clock, connection, apiAddress, oidcConfiguration, limiterOff),
            apiAddress: apiAddress ?? "https://decisya-api",
            eagerSettings: (overrides ?? [])
                .Where(pair => pair.Key.StartsWith("Bff:DataProtection:", StringComparison.Ordinal))
                .ToDictionary(pair => pair.Key, pair => pair.Value));
    }

    internal HttpClient CreateBffClient() =>
        new(Server.CreateHandler()) { BaseAddress = new Uri("https://localhost:7200") };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            var values = TestConfiguration.GoodOverrides();
            if (redisConnectionString is not null)
            {
                values["ConnectionStrings:redis"] = redisConnectionString;
            }

            if (apiAddress is not null)
            {
                values["Bff:Api:Address"] = apiAddress;
            }

            if (edge is not null)
            {
                values["Decisya:Edge:TrustedProxies"] = edge.ToString();
            }

            foreach (var pair in overrides)
            {
                values[pair.Key] = pair.Value;
            }

            configuration.AddInMemoryCollection(values);
        });

        builder.ConfigureServices(services =>
        {
            services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter>(new PeerStartupFilter(edge));
            services.Configure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, options =>
                options.Configuration = oidcConfiguration ?? new OpenIdConnectConfiguration
                {
                    Issuer = "https://example.test/realms/decisya",
                    AuthorizationEndpoint = "https://example.test/realms/decisya/protocol/openid-connect/auth",
                    TokenEndpoint = "https://example.test/realms/decisya/protocol/openid-connect/token",
                });

            if (limiterOff)
            {
                // NFR-53 baseline only: the limiter middleware stays in the pipeline, but no limiter is evaluated.
                services.PostConfigure<Microsoft.AspNetCore.RateLimiting.RateLimiterOptions>(options => options.GlobalLimiter = null);
            }

            if (clock is not null)
            {
                services.AddSingleton(clock);
            }

            if (loggerProvider is not null)
            {
                services.AddSingleton(loggerProvider);
                // Debug for the capturing provider only (a rule that names the provider wins over the
                // category rules); the console keeps its configured levels, so a failure prints no noise.
                services.PostConfigure<LoggerFilterOptions>(options =>
                    options.Rules.Add(new LoggerFilterRule(
                        providerName: loggerProvider.GetType().FullName, categoryName: null, logLevel: LogLevel.Debug, filter: null)));
            }
        });
    }

    private sealed class PeerStartupFilter(IPAddress? edge) : Microsoft.AspNetCore.Hosting.IStartupFilter
    {
        public Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> Configure(
            Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> next) =>
            app =>
            {
                app.Use(async (context, nextMiddleware) =>
                {
                    if (context.Request.Headers.TryGetValue(PeerHeader, out var value) && IPAddress.TryParse(value.ToString(), out var peer))
                    {
                        context.Connection.RemoteIpAddress = peer;
                    }

                    await nextMiddleware(context);
                });

                if (edge is not null)
                {
                    var forwarded = new ForwardedHeadersOptions
                    {
                        ForwardedHeaders = ForwardedHeaders.XForwardedFor,
                        ForwardLimit = 1,
                        RequireHeaderSymmetry = false,
                    };
                    forwarded.KnownProxies.Clear();
                    forwarded.KnownIPNetworks.Clear();
                    forwarded.KnownProxies.Add(edge);
                    app.UseForwardedHeaders(forwarded);
                }

                next(app);
            };
    }
}

internal static class RateLimitRequests
{
    internal static HttpRequestMessage Get(string path, string? peer = null, string? forwardedFor = null, string? cookie = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        Decorate(request, peer, forwardedFor, cookie);
        return request;
    }

    internal static HttpRequestMessage PostForm(string path, Dictionary<string, string> form, string? peer = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new FormUrlEncodedContent(form) };
        Decorate(request, peer, null, null);
        return request;
    }

    internal static async Task<HttpStatusCode> StatusAsync(HttpClient client, HttpRequestMessage request)
    {
        using (request)
        using (var response = await client.SendAsync(request, TestContext.Current.CancellationToken))
        {
            return response.StatusCode;
        }
    }

    internal static Dictionary<string, string?> Limits(
        int? login = null, int? backchannel = null, int? api = null, int? anonymous = null, int? admin = null, int? windowSeconds = null)
    {
        var values = new Dictionary<string, string?>();
        void Set(string cls, string setting, int? value)
        {
            if (value is not null)
            {
                values[$"Bff:RateLimits:{cls}:{setting}"] = value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        Set("login", "PermitLimit", login);
        Set("backchannel_logout", "PermitLimit", backchannel);
        Set("api", "PermitLimit", api);
        Set("api", "AnonymousPermitLimit", anonymous);
        Set("admin", "PermitLimit", admin);
        foreach (var cls in new[] { "login", "backchannel_logout", "api", "admin" })
        {
            Set(cls, "WindowSeconds", windowSeconds);
        }

        return values;
    }

    private static void Decorate(HttpRequestMessage request, string? peer, string? forwardedFor, string? cookie)
    {
        if (peer is not null)
        {
            request.Headers.TryAddWithoutValidation(RateLimitFactory.PeerHeader, peer);
        }

        if (forwardedFor is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", forwardedFor);
        }

        if (cookie is not null)
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
        }
    }
}
