using Decisya.Bff;
using Decisya.Bff.Endpoints;
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

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddOpenIdConnect(OpenIdConnectDefaults.AuthenticationScheme, static _ => { });

// Configures the handlers registered just above (CookieOptionsSetup wires in
// RedisTicketStore; OidcOptionsSetup maps BffOptions onto the OIDC handler).
builder.Services.ConfigureOptions<CookieOptionsSetup>();
builder.Services.ConfigureOptions<OidcOptionsSetup>();

builder.Services.AddAuthorization();

builder.Services.AddAntiforgery(options =>
{
    options.Cookie.Name = AntiforgeryCookieNames.CookieToken;
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.Path = "/";
    options.HeaderName = AntiforgeryCookieNames.HeaderName;
});

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

app.Run();
