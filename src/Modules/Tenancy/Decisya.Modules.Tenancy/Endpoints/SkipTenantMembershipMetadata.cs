namespace Decisya.Modules.Tenancy.Endpoints;

/// <summary>
/// Marks an endpoint as exempt from <see cref="TenantMembershipMiddleware"/> (issue #21, G2).
/// Applied through <c>TenancyModule.SkipTenantMembership</c>. As of #21 the only holder is
/// <c>/api/whoami</c> (identity-dev).
/// </summary>
internal sealed class SkipTenantMembershipMetadata
{
    public static readonly SkipTenantMembershipMetadata Instance = new();

    private SkipTenantMembershipMetadata()
    {
    }
}
