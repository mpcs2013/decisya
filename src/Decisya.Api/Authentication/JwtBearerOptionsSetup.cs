using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Decisya.Api.Authentication;

/// <summary>
/// Configures the API's only authentication scheme (G2 "JwtBearerOptions" table): validation
/// against the configured Keycloak authority, the <c>decisya-api</c> audience, a small clock
/// skew, and an RS256/ES256 algorithm allow-list. Reads <see cref="ApiJwtOptions"/> through
/// <see cref="IOptionsMonitor{TOptions}"/> rather than capturing a snapshot, so a config reload
/// (tests included) is honoured; <see cref="ApiJwtOptionsEnvironmentValidator"/> is the
/// fail-closed gate on the raw values, this class only maps them onto the handler.
/// </summary>
internal sealed class JwtBearerOptionsSetup(
    IOptionsMonitor<ApiJwtOptions> apiJwtOptions, IHostEnvironment environment, ILoggerFactory loggerFactory)
    : IConfigureNamedOptions<JwtBearerOptions>
{
    private const string LoggerCategory = "Decisya.Api.Authentication.JwtBearerOptionsSetup";

    public void Configure(string? name, JwtBearerOptions options) => Configure(options);

    public void Configure(JwtBearerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var jwt = apiJwtOptions.CurrentValue;

        options.Authority = jwt.Authority;

        // Mirrors Decisya.Bff.Session.OidcOptionsSetup's rule exactly (#18): the relaxation
        // requires Development AND the flag AND a loopback authority host, read from the flag
        // itself, never assumed.
        options.RequireHttpsMetadata =
            !(environment.IsDevelopment() && !jwt.RequireHttpsMetadata && IsLoopbackAuthority(jwt.Authority));

        // #120 G4-120-05 (B-3): the discovery document and the JWKS are fetched through this
        // handler (the ConfigurationManager is built over the options' Backchannel), so one pinned
        // handler covers every back-channel request. Unset (Development): the framework's
        // default handler, unchanged.
        if (BackchannelRoot.LoadOrNull(jwt.TrustedRootPath, ApiJwtOptions.TrustedRootPathKey) is { } trustedRoot)
        {
            options.BackchannelHttpHandler = BackchannelRoot.CreateHandler(trustedRoot);
        }

        options.MapInboundClaims = false;

        // The token never enters AuthenticationProperties (T-11): this handler never saves one.
        options.SaveToken = false;

        // Story 2 / T-06: every failure gives the bare "WWW-Authenticate: Bearer" challenge,
        // with no error, error_description or claim value.
        options.IncludeErrorDetails = false;

        var parameters = options.TokenValidationParameters;
        parameters.ValidateIssuer = true;
        // S-1 (T-05): pin ValidIssuer to the configured authority as well as taking it from
        // discovery, so a metadata document that names another issuer cannot widen trust, and
        // harness A's wrong-iss row does not depend on the static configuration manager.
        parameters.ValidIssuer = NormalizeIssuer(jwt.Authority);
        parameters.ValidateAudience = true;
        parameters.ValidAudience = ApiJwtDefaults.Audience;
        parameters.ValidateLifetime = true;
        parameters.RequireExpirationTime = true;
        parameters.RequireSignedTokens = true;
        parameters.ValidateIssuerSigningKey = true;
        // G4-20-01: the allow-list itself. alg=none fails on RequireSignedTokens above; classic
        // HS256-with-the-public-key fails both here and because IdentityModel will not treat an
        // RsaSecurityKey as an HMAC key.
        parameters.ValidAlgorithms = [SecurityAlgorithms.RsaSha256, SecurityAlgorithms.EcdsaSha256];
        // NFR-27: 60 s, not the handler's 300 s default, which stacked on the <=300 s maximum
        // access-token lifespan would accept a token up to 5 minutes past its stated expiry.
        parameters.ClockSkew = TimeSpan.FromSeconds(60);
        parameters.NameClaimType = "sub";

        var logger = loggerFactory.CreateLogger(LoggerCategory);

        options.Events = new JwtBearerEvents
        {
            // #121 G4-121-04 (a), ASVS V16.3.1: a rejected bearer token is one Warning with a closed
            // reason. Never fires for a missing token, and never carries the token, a header value, a
            // claim or the exception's text. The 401 and its bare challenge stay as they are (NFR-26).
            OnAuthenticationFailed = context =>
            {
                AuthEventsLog.TokenRejected(
                    logger, TokenRejectionReason.From(context.Exception, context.Request.Headers.Authorization.ToString()));
                return Task.CompletedTask;
            },

            // G3 point (T-03, ASVS V9.2): reject any token whose "typ" claim is not exactly
            // "Bearer" at the authentication stage, so the result is 401, not 403. context.Fail
            // writes no body of its own (G2's rule about events); IncludeErrorDetails=false
            // still governs the resulting challenge. Fail does not raise OnAuthenticationFailed,
            // so this branch logs its own event (reason bad_type, T121-08).
            OnTokenValidated = context =>
            {
                var typClaim = context.Principal?.FindFirst("typ")?.Value;
                if (!string.Equals(typClaim, "Bearer", StringComparison.Ordinal))
                {
                    AuthEventsLog.TokenRejected(logger, TokenRejectionReason.BadType);
                    context.Fail("The token type is not accepted.");
                }

                return Task.CompletedTask;
            },
        };
    }

    private static string? NormalizeIssuer(string? authority) =>
        string.IsNullOrWhiteSpace(authority) ? authority : authority.TrimEnd('/');

    private static bool IsLoopbackAuthority(string? authority) =>
        Uri.TryCreate(authority, UriKind.Absolute, out var uri)
        && (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Host, "127.0.0.1", StringComparison.Ordinal)
            || string.Equals(uri.Host, "::1", StringComparison.Ordinal));
}
