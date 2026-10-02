using System.Text.Json.Serialization;
using Decisya.SharedKernel.Observability;

namespace Decisya.Modules.Admin.Contracts;

/// <summary>
/// The JSON body of <c>PUT /api/admin/tenants/{tenantId}/overrides/{featureKey}</c> (issue #25,
/// G2): <c>{ "reason": string, "expiresAt": string | null }</c>. Unknown members are refused.
/// <see cref="ExpiresAt"/> is an RFC 3339 UTC instant with the <c>Z</c> designator
/// (<c>2026-12-31T23:59:59Z</c>), parsed by the Admin endpoint with NodaTime's extended ISO
/// pattern; anything else is a 400.
/// </summary>
/// <remarks>
/// <see cref="Reason"/> is free text an operator may fill with personal data. It is
/// <see cref="SensitiveAttribute">[Sensitive]</see>, and <see cref="ToString"/> never renders it,
/// so neither a structured log nor an interpolated string can carry it.
/// </remarks>
public sealed record OverrideGrantRequest(
    [property: Sensitive, JsonPropertyName("reason")] string? Reason,
    [property: JsonPropertyName("expiresAt")] string? ExpiresAt)
{
    /// <summary>Never renders <see cref="Reason"/> or <see cref="ExpiresAt"/>.</summary>
    public override string ToString() => nameof(OverrideGrantRequest);
}
