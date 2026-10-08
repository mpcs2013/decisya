using System.Diagnostics;
using Decisya.Bff;
using Decisya.Bff.Endpoints;
using Decisya.Bff.KeyRing;
using Decisya.Bff.Proxy;
using Decisya.Bff.RateLimiting;
using Decisya.Bff.Security;
using Decisya.Bff.Session;
using Decisya.Bff.Spa;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;

// #122 G4-122-03: the certificate generator mode runs before anything else (no configuration, logging
// or hosted service) and only for the exact argument list [--generate-keyring-certificate].
KeyRingCertificateGenerator.ExitIfRequested(args);

// #120: the Compose healthcheck runs this binary with --health-probe (no shell in the image).
HealthProbe.ExitIfRequested(args);

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// #27 B-01: OpenTelemetry's ASP.NET Core instrumentation must not re-extract the browser's
// context either (process-wide switch, opt-in; the Api keeps normal propagation).
builder.UseInboundTraceRoots();

// #27 B-01: the hosting diagnostics read the propagator from DI (TryAdd, so this wins), and
// this one ignores inbound traceparent, tracestate and baggage: a new trace root per request.
builder.Services.AddSingleton<DistributedContextPropagator, BffTracePropagator>();

builder.Services.AddProblemDetails();

builder.Services.AddOptions<BffOptions>()
    .Bind(builder.Configuration.GetSection(BffOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<BffOptions>, BffOptionsEnvironmentValidator>();
// #120 G4-120-05: a configured back-channel root must be one usable CA certificate, in every
// environment, or the host does not start.
builder.Services.AddSingleton<IValidateOptions<BffOptions>, BackchannelTrustOptionsValidator>();
builder.Services.AddSingleton<BackchannelTrust>();

// D1: the key ring lives on the file system, never in Redis. Unset in Development, the
// framework default (%LOCALAPPDATA%\ASP.NET\DataProtection-Keys, DPAPI) applies.
// BffOptionsEnvironmentValidator requires the path, and KeyRingOptionsValidator the certificate,
// outside Development (#122: certificate-wrapped keys, a 90-day lifetime, a fail-closed start-up check).
builder.AddBffDataProtection();

// #122: the rate limiter (C-09a): the limits, the partition step and the global chained limiter.
builder.Services.AddBffRateLimiting();

builder.AddBffRedisClient();

builder.Services.AddSingleton<RedisTicketStore>();
builder.Services.AddSingleton<LogoutTokenValidator>();

// #19 G2: refresh state machine and B-2 server-side logout.
builder.Services.AddSingleton<RedisRefreshLock>();
builder.Services.AddSingleton<KeycloakTokenClient>();
builder.Services.AddSingleton<AccessTokenProvider>();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddOpenIdConnect(OpenIdConnectDefaults.AuthenticationScheme, static _ => { });

// Configures the handlers registered just above (CookieOptionsSetup wires in
// RedisTicketStore; OidcOptionsSetup maps BffOptions onto the OIDC handler).
builder.Services.ConfigureOptions<CookieOptionsSetup>();
builder.Services.ConfigureOptions<OidcOptionsSetup>();

builder.Services.AddAuthorization(options =>
{
    // #19 G2: the policy the /api route's RouteConfig.AuthorizationPolicy names — an
    // authenticated caller only; an anonymous /api call gets 401 (never a login redirect),
    // via #18's CookieOptionsSetup.OnRedirectToLogin.
    options.AddPolicy(ProxyConfiguration.DefaultAuthorizationPolicyName, policy => policy.RequireAuthenticatedUser());
});

builder.Services.AddAntiforgery(options =>
{
    options.Cookie.Name = AntiforgeryCookieNames.CookieToken;
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.Path = "/";
    options.HeaderName = AntiforgeryCookieNames.HeaderName;
});

// #19 G2: one route, one cluster, both built in code — never from an appsettings*.json
// "ReverseProxy" section. The raw configuration value is read here (before Build()) only to
// seed the cluster's destination; BffOptionsEnvironmentValidator separately enforces the
// https-outside-Development invariant on the same key through ValidateOnStart.
var apiAddress = builder.Configuration["Bff:Api:Address"] ?? string.Empty;
builder.Services.AddApiReverseProxy(apiAddress);

var app = builder.Build();

// #26 D6 / G3 S-b: the security headers on every response, so first: the exception handler's
// 500, the 401s, the redirects and the proxied /api responses all carry them.
// #120: forwarded headers come before even that, trusted from Caddy's address only, so everything
// below (HSTS, the OIDC redirect URIs, Secure cookies) sees the real client scheme and address.
app.UseDecisyaForwardedHeaders();
app.UseSecurityHeaders();
app.UseExceptionHandler();

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

// #26 D5: /assets/* only, before the session guard and authentication, so an asset request never
// touches the cookie handler or Redis.
app.UseSpaStaticAssets();
app.WarnIfIndexMissing();

// #122 G2 D1: the partition step and the rate limiter, after forwarded headers (the client address is
// real) and before the session guard, authentication, authorization and the proxy, so a refused request
// does no fixation work, no auth, no Api call and no token refresh. The OIDC callback is inside
// UseAuthentication, which is why this cannot be endpoint policies.
app.UseBffRateLimiting();

// G4-18-05 (T-05): must run before UseAuthentication(), so the cookie handler's own
// AuthenticateAsync never sees a session cookie left over from a different user.
app.Use(async (context, next) =>
{
    await SessionFixationGuard.RemoveExistingSessionOnCallbackAsync(context);
    await next(context);
});

app.UseAuthentication();
app.UseAuthorization();

app.MapDefaultEndpoints();
app.MapBffEndpoints();

// #19 G2: UseAuthentication()/UseAuthorization() above already gate anonymous /api callers
// (401, route policy "default") before any of this runs. Order inside the proxy pipeline
// matters: antiforgery (B-3) before a token is even requested, then the refresh state
// machine, then YARP's own steps (its custom-pipeline docs require them to be added
// explicitly here).
app.MapReverseProxy(proxyPipeline =>
{
    proxyPipeline.UseMiddleware<UpgradeRejectionMiddleware>();
    proxyPipeline.UseMiddleware<ApiAntiforgeryMiddleware>();
    proxyPipeline.UseMiddleware<AccessTokenMiddleware>();
    proxyPipeline.UseSessionAffinity();
    proxyPipeline.UseLoadBalancing();
    proxyPipeline.UsePassiveHealthChecks();
}).WithMetadata(new RouteClassMetadata(RouteClass.Api)); // #122: /api/* is the api class (admin by path)

// #26 D5: the SPA fallback is mapped last (lowest precedence); GET/HEAD only, anonymous, and the
// reserved server prefixes stay 404.
app.MapSpaFallback();

app.Run();
