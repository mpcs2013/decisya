namespace Decisya.Modules.Tenancy.Contracts;

/// <summary>One row of <c>GET /api/tenancy/members</c> (issue #21, G2, Story 3). <c>Role</c> is <c>"Owner"</c> or <c>"Member"</c>.</summary>
public sealed record MemberDto(string UserId, string Role);
