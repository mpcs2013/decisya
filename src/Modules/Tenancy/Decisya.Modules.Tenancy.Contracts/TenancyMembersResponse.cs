namespace Decisya.Modules.Tenancy.Contracts;

/// <summary>The response body of <c>GET /api/tenancy/members</c> (issue #21, G2, Story 3), ordered by <c>created_at</c> then <c>user_id</c>.</summary>
public sealed record TenancyMembersResponse(IReadOnlyList<MemberDto> Members);
