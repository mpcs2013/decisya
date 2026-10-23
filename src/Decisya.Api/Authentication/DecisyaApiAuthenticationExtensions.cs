using Decisya.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Ostium.Authentication.Jwt;

namespace Decisya.Api.Authentication;

/// <summary>
/// The single entry point <c>Program</c> calls. The token validation (JwtBearer scheme, fallback
/// policy, 403 shape) comes from <c>Ostium.Authentication.Jwt</c>; this adds only what is
/// Decisya's: the admin-MFA policy and the scoped request caller. It sets no scheme setting and
/// no fallback policy after Ostium's call (the Bearer scheme is locked).
/// </summary>
public static class DecisyaApiAuthenticationExtensions
{
    public static WebApplicationBuilder AddDecisyaApiAuthentication(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Ostium: options and validators (ValidateOnStart), the Bearer scheme, the fallback policy
        // and the generic 403 handler.
        builder.AddApiAuthentication();

        // #121 G4-121-01 (d): Authentication:RequireAdminMfa defaults to true; false, or a value that is
        // not a boolean, fails start-up outside Development (the key is named, the value never).
        builder.Services.AddOptions<AdminMfaOptions>().ValidateOnStart();
        builder.Services.AddSingleton<IConfigureOptions<AdminMfaOptions>, AdminMfaOptionsSetup>();
        builder.Services.AddSingleton<IValidateOptions<AdminMfaOptions>, AdminMfaOptionsEnvironmentValidator>();

        builder.Services.AddAuthorization(options =>
        {
            // #121 G2 D3: the MFA proof, a second policy on the /api/admin group next to
            // Admin.PlatformAdmin. It repeats the two requirements a group policy replaces the
            // fallback's with (an authenticated user with a sub claim), then its own.
            options.AddPolicy(AdminMfaPolicy.Name, policy => policy
                .RequireAuthenticatedUser()
                .RequireClaim("sub")
                .AddRequirements(new AdminMfaRequirement()));
        });
        builder.Services.AddScoped<IAuthorizationHandler, AdminMfaAuthorizationHandler>();

        // #21, G2 D5; G3 G4-21-01 (closes #22 B-1): exactly one scoped RequestCaller, with
        // ICurrentTenant and ICurrentCaller both forwarded to that same instance — never a
        // separate registration for either, and never singleton or transient. Populated once
        // per request by CallerContextMiddleware.
        builder.Services.AddScoped<RequestCaller>();
        builder.Services.AddScoped<ICurrentTenant>(static sp => sp.GetRequiredService<RequestCaller>());
        builder.Services.AddScoped<ICurrentCaller>(static sp => sp.GetRequiredService<RequestCaller>());

        return builder;
    }
}
