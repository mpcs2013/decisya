using Microsoft.AspNetCore.Authorization;

namespace Decisya.Modules.Tenancy.Endpoints;

/// <summary>The <c>Tenancy.Owner</c> authorization policy (issue #21, Story 3; G2).</summary>
internal static class TenancyPolicies
{
    public const string OwnerPolicyName = "Tenancy.Owner";
}

/// <summary>Marker requirement for <see cref="TenancyPolicies.OwnerPolicyName"/>. Carries no data: the handler reads the caller's own membership.</summary>
internal sealed class TenantOwnerRequirement : IAuthorizationRequirement;
