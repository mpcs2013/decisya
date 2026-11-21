using Decisya.Modules.Entitlements.Contracts;
using Microsoft.AspNetCore.Authorization;

namespace Decisya.Api.Authorization;

/// <summary>
/// "The current tenant is entitled to <see cref="Feature"/>" (issue #341, G2 D4; ADR-0008). One requirement per
/// <see cref="FeatureKey"/>, carried by the policy <see cref="EntitlementPolicies.NameFor"/> names.
/// </summary>
internal sealed class EntitlementRequirement(FeatureKey feature) : IAuthorizationRequirement
{
    public FeatureKey Feature { get; } = feature;
}

/// <summary>
/// Succeeds only when <see cref="IEntitlementService.IsEnabledAsync"/> is <see langword="true"/> for the
/// requirement's key. Under a tenant resolution of <c>None</c> or <c>Invalid</c> the service answers
/// <see langword="false"/> with no query (NFR-35), so a caller with no tenant is refused before any module query.
/// An exception propagates as the generic 500, never as "allowed" (#23 C-3). Registered scoped, so it sees the
/// request's own service. The Information line names the feature key only; no tenant or user value.
/// </summary>
internal sealed partial class EntitlementAuthorizationHandler(
    IEntitlementService entitlements,
    ILogger<EntitlementAuthorizationHandler> logger) : AuthorizationHandler<EntitlementRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, EntitlementRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        var cancellationToken = context.Resource is HttpContext http ? http.RequestAborted : CancellationToken.None;

        if (await entitlements.IsEnabledAsync(requirement.Feature, cancellationToken).ConfigureAwait(false))
        {
            context.Succeed(requirement);
            return;
        }

        EntitlementDenied(logger, requirement.Feature);
    }

    [LoggerMessage(
        EventId = 2101,
        EventName = "entitlement.denied",
        Level = LogLevel.Information,
        Message = "entitlement denied: feature {FeatureKey}.")]
    private static partial void EntitlementDenied(ILogger logger, FeatureKey featureKey);
}

/// <summary>The one registration entry point for the entitlement policies.</summary>
public static class EntitlementAuthorizationExtensions
{
    /// <summary>
    /// Registers, for every key in <see cref="FeatureKeys.All"/>, the policy
    /// <see cref="EntitlementPolicies.NameFor"/> and the scoped handler. A group policy replaces the fallback
    /// policy's requirements, so each entitlement policy is built from the live fallback policy (its
    /// requirements and its authentication schemes) and then adds <see cref="EntitlementRequirement"/>; it also
    /// repeats <c>RequireAuthenticatedUser()</c> and <c>RequireClaim("sub")</c>, so the policy holds those even if
    /// the fallback is absent (G3 M341-01 (c): the expected set is read from the live fallback at run time).
    /// The policies are added in a post-configure step, so the fallback is already set whatever the order of
    /// the <c>AddAuthorization</c> calls.
    /// </summary>
    public static IServiceCollection AddEntitlementPolicies(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddAuthorization();
        services.AddOptions<AuthorizationOptions>().PostConfigure(options =>
        {
            foreach (var feature in FeatureKeys.All)
            {
                var builder = new AuthorizationPolicyBuilder();

                if (options.FallbackPolicy is { } fallback)
                {
                    builder.Combine(fallback);
                }

                options.AddPolicy(
                    EntitlementPolicies.NameFor(feature),
                    builder
                        .RequireAuthenticatedUser()
                        .RequireClaim("sub")
                        .AddRequirements(new EntitlementRequirement(feature))
                        .Build());
            }
        });

        services.AddScoped<IAuthorizationHandler, EntitlementAuthorizationHandler>();
        return services;
    }
}
