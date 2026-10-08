using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace Decisya.Api.Authentication;

/// <summary>
/// The second requirement on <c>/api/admin</c> (issue #121, G2 D3, G3 G4-121-01): the access token
/// proves MFA. Together with the Admin module's <c>Admin.PlatformAdmin</c> it is an AND: ASP.NET Core
/// combines the two policies on the group, so both must succeed.
/// </summary>
internal sealed class AdminMfaRequirement : IAuthorizationRequirement;

/// <summary>
/// Succeeds when <c>Authentication:RequireAdminMfa</c> is false (Development only, validated at
/// start-up), or when the caller is a tenant-less platform admin and the validated token has exactly
/// one string <c>acr</c> claim equal to "2" (<see cref="CallerIdentity.HasMfaLevel"/>, parsed once).
/// A platform admin without it is refused with the generic 403 and one Warning
/// (<c>auth.admin.mfa_required</c>, hashed user id from the request's enrichment). A caller who is
/// not a platform admin fails silently here: the Admin handler already logs that refusal. No
/// network call, no database command: the claim comes from the already validated token.
/// </summary>
internal sealed class AdminMfaAuthorizationHandler(
    RequestCaller caller,
    IOptionsMonitor<AdminMfaOptions> options,
    ILogger<AdminMfaAuthorizationHandler> logger) : AuthorizationHandler<AdminMfaRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, AdminMfaRequirement requirement)
    {
        if (!options.CurrentValue.RequireAdminMfa)
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        if (caller.IsPlatformAdmin && caller.HasMfaLevel)
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        if (caller.IsPlatformAdmin)
        {
            AuthEventsLog.AdminMfaRequired(logger);
        }

        return Task.CompletedTask;
    }
}

/// <summary>The <c>Api.AdminMfa</c> policy and the one extension that puts it on the admin group.</summary>
public static class AdminMfaPolicy
{
    /// <summary>The policy name; every <c>/api/admin</c> endpoint must carry it next to <c>Admin.PlatformAdmin</c>.</summary>
    public const string Name = "Api.AdminMfa";

    /// <summary>
    /// Requires the MFA proof on every endpoint of <paramref name="group"/>. <c>Program.cs</c> calls
    /// it next to <c>SkipTenantMembership()</c> on the admin group; the coverage test fails if an
    /// <c>/api/admin</c> endpoint ever lacks it.
    /// </summary>
    public static RouteGroupBuilder RequireAdminMfa(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        return group.RequireAuthorization(Name);
    }
}
