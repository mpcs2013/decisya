using Decisya.Bff;
using Decisya.Bff.Endpoints;
using Decisya.Bff.Proxy;
using Decisya.Bff.Security;
using Decisya.Bff.Session;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddProblemDetails();

builder.Services.AddOptions<BffOptions>()
    .Bind(builder.Configuration.GetSection(BffOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<BffOptions>, BffOptionsEnvironmentValidator>();

// D1: the key ring lives on the file system, never in Redis. Unset in Development, the
// framework default (%LOCALAPPDATA%\ASP.NET\DataProtection-Keys, DPAPI) applies.
// BffOptionsEnvironmentValidator requires this outside Development.
var dataProtectionBuilder = builder.Services.AddDataProtection().SetApplicationName("Decisya.Bff");
var keyRingPath = builder.Configuration["Bff:DataProtection:KeyRingPath"];
if (!string.IsNullOrWhiteSpace(keyRingPath))
{
    dataProtectionBuilder.PersistKeysToFileSystem(new DirectoryInfo(keyRingPath));
}

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

app.UseExceptionHandler();

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

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
});

app.Run();
