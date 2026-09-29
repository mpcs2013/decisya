using System.Text.Json.Serialization;

namespace Decisya.Modules.Tenancy.Contracts;

/// <summary>
/// The response body of <c>GET /api/tenancy/me</c> (issue #21, G2, Story 2). Both properties
/// are omitted from the JSON entirely (never written as <c>null</c>) for a caller resolved to
/// <c>None</c> (a platform admin), so the wire body has no "tenant" and no "membership" field.
/// </summary>
public sealed record TenancyMeResponse(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TenantDto? Tenant,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] MembershipDto? Membership);
