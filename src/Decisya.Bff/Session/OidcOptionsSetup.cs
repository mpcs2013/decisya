using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Decisya.Bff.Session;

/// <summary>
/// Configures the default challenge scheme (G2 "OIDC scheme" table): authorization code +
/// PKCE S256 against <c>decisya-bff</c>, tokens saved into the ticket only (never the
/// cookie), and the correlation/nonce cookies' own flags. Reads <see cref="BffOptions"/>
/// through <see cref="IOptionsMonitor{TOptions}"/> rather than capturing a snapshot, so a
/// config reload (tests included) is honoured; <see cref="BffOptionsEnvironmentValidator"/>
/// is the fail-closed gate on the raw values, this class only maps them onto the handler.
/// </summary>
internal sealed class OidcOptionsSetup(IOptionsMonitor<BffOptions> bffOptions, IHostEnvironment environment)
    : IConfigureNamedOptions<OpenIdConnectOptions>
{
    public void Configure(string? name, OpenIdConnectOptions options) => Configure(options);

    public void Configure(OpenIdConnectOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var bff = bffOptions.CurrentValue;

        options.Authority = bff.Oidc.Authority;
        options.ClientId = bff.Oidc.ClientId;
        options.ClientSecret = bff.Oidc.ClientSecret;

        // S-5 (T-08): the Development relaxation additionally requires a loopback authority
        // host, read as IsDevelopment() && flag, never from the flag alone (G4-18-04).
        options.RequireHttpsMetadata =
            !(environment.IsDevelopment() && !bff.Oidc.RequireHttpsMetadata && IsLoopbackAuthority(bff.Oidc.Authority));

        options.ResponseType = OpenIdConnectResponseType.Code;
        // S-1 (T-16, T-03): explicit query mode. The handler default, form_post, is a
        // cross-site POST; under schemeful same-site an http dev/Testcontainers Keycloak ->
        // https BFF callback would otherwise drop the SameSite=Lax correlation/nonce cookies.
        options.ResponseMode = OpenIdConnectResponseMode.Query;
        options.UsePkce = true;

        options.Scope.Clear();
        options.Scope.Add("openid");
        options.Scope.Add("profile");
        options.Scope.Add("email");

        // The tokens go into the Redis-backed ticket (RedisTicketStore), never the cookie.
        options.SaveTokens = true;
        options.GetClaimsFromUserInfoEndpoint = false;
        options.MapInboundClaims = false;

        options.TokenValidationParameters.NameClaimType = "sub";
        options.TokenValidationParameters.RoleClaimType = "roles";
        options.TokenValidationParameters.ValidAlgorithms = ["RS256", "ES256"];

        ConfigureCorrelationAndNonceCookies(options.CorrelationCookie);
        ConfigureCorrelationAndNonceCookies(options.NonceCookie);

        // S-7: the realm has frontchannelLogout: false; this removes the otherwise-unused
        // GET front-channel sign-out endpoint.
        options.RemoteSignOutPath = PathString.Empty;

        options.Events.OnRedirectToIdentityProviderForSignOut = context =>
        {
            // D3: no id_token_hint. A Location header carrying the ID token would break the
            // Done-when and NFR-21; Keycloak 26 shows its own logout-confirmation page instead.
            context.ProtocolMessage.IdTokenHint = null;
            context.ProtocolMessage.ClientId = context.Options.ClientId;
            return Task.CompletedTask;
        };
    }

    private static void ConfigureCorrelationAndNonceCookies(CookieBuilder cookie)
    {
        cookie.SameSite = SameSiteMode.Lax;
        cookie.SecurePolicy = CookieSecurePolicy.Always;
        cookie.HttpOnly = true;
    }

    private static bool IsLoopbackAuthority(string? authority) =>
        Uri.TryCreate(authority, UriKind.Absolute, out var uri)
        && (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Host, "127.0.0.1", StringComparison.Ordinal)
            || string.Equals(uri.Host, "::1", StringComparison.Ordinal));
}
