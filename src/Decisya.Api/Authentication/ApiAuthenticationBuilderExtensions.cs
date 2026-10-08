using Decisya.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Decisya.Api.Authentication;

/// <summary>
/// The single entry point <c>Program</c> calls (G2): it never references <c>JwtBearer</c> or
/// <c>Microsoft.IdentityModel</c> types directly, so the NetArchTest rule confining those
/// namespaces to <c>Decisya.Api.Authentication</c> holds.
/// </summary>
public static class ApiAuthenticationBuilderExtensions
{
    public static WebApplicationBuilder AddApiAuthentication(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddOptions<ApiJwtOptions>()
            .Bind(builder.Configuration.GetSection(ApiJwtOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<ApiJwtOptions>, ApiJwtOptionsEnvironmentValidator>();
        // #120 G4-120-05: a configured back-channel root must be one usable CA certificate, in
        // every environment, or the host does not start.
        builder.Services.AddSingleton<IValidateOptions<ApiJwtOptions>, ApiJwtTrustedRootValidator>();

        // #121 G4-121-01 (d): Authentication:RequireAdminMfa defaults to true; false, or a value that is
        // not a boolean, fails start-up outside Development (the key is named, the value never).
        builder.Services.AddOptions<AdminMfaOptions>().ValidateOnStart();
        builder.Services.AddSingleton<IConfigureOptions<AdminMfaOptions>, AdminMfaOptionsSetup>();
        builder.Services.AddSingleton<IValidateOptions<AdminMfaOptions>, AdminMfaOptionsEnvironmentValidator>();

        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, static _ => { });
        builder.Services.ConfigureOptions<JwtBearerOptionsSetup>();

        builder.Services.AddAuthorization(options =>
        {
            // D2: every endpoint, and every unmatched path, requires an authenticated caller
            // with a "sub" claim. The only AllowAnonymous endpoints are /health and /alive.
            options.FallbackPolicy = new AuthorizationPolicyBuilder(JwtBearerDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser()
                .RequireClaim("sub")
                .Build();

            // #121 G2 D3: the MFA proof, a second policy on the /api/admin group next to
            // Admin.PlatformAdmin. It repeats the two requirements a group policy replaces the
            // fallback's with (an authenticated user with a sub claim), then its own.
            options.AddPolicy(AdminMfaPolicy.Name, policy => policy
                .RequireAuthenticatedUser()
                .RequireClaim("sub")
                .AddRequirements(new AdminMfaRequirement()));
        });
        builder.Services.AddScoped<IAuthorizationHandler, AdminMfaAuthorizationHandler>();

        // #25, G2 D5; G3 G4-25-03: one 403 shape for every policy. Replace, not add, so exactly one
        // IAuthorizationMiddlewareResultHandler exists whatever AddAuthorization registered.
        builder.Services.Replace(ServiceDescriptor.Singleton<IAuthorizationMiddlewareResultHandler, ProblemDetailsAuthorizationResultHandler>());

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
