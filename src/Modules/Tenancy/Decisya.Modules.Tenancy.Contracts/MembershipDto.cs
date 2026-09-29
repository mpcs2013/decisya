namespace Decisya.Modules.Tenancy.Contracts;

/// <summary>The caller's own membership role, as returned by <c>GET /api/tenancy/me</c> (issue #21, G2). <c>Role</c> is <c>"Owner"</c> or <c>"Member"</c>.</summary>
public sealed record MembershipDto(string Role);
