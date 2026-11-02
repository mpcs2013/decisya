using System.Text;
using Decisya.SharedKernel.Observability;

namespace Decisya.Modules.Tenancy.Contracts;

/// <summary>One row of <c>GET /api/tenancy/members</c> (issue #21, G2, Story 3). <c>Role</c> is <c>"Owner"</c> or <c>"Member"</c>.</summary>
public sealed record MemberDto([property: Sensitive] string UserId, string Role)
{
    // Issue #230, G2 D5: [Sensitive] masks only structured log state, so the synthesized ToString must never
    // render the raw sub either (string interpolation, exception messages). Same pattern as GrantOverride.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Role = ").Append(Role);
        return true;
    }
}
