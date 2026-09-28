using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
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
        });

        return builder;
    }
}
