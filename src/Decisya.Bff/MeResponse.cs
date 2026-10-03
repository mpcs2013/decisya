using System.Text.Json.Serialization;

namespace Decisya.Bff;

/// <summary>
/// The <c>GET /bff/me</c> response (G1 Story 5, G2). A <c>sealed record</c> here rather than
/// a <c>Contracts</c> project — no module consumes it (G2). Identity-only; ADR-0008 amendment 1:
/// the capability manifest is <c>GET /api/capabilities</c>, never a member of this shape.
/// Never carries a token.
/// </summary>
/// <remarks>
/// <see cref="TenantId"/>, <see cref="Sub"/>, <see cref="Email"/> and <see cref="Roles"/> are
/// omitted from the JSON body — not rendered as <c>null</c> or empty — whenever they do not
/// apply: for an anonymous caller (<see cref="IsAuthenticated"/> <see langword="false"/>,
/// Story 5's second scenario), and for <see cref="TenantId"/> on a platform-admin caller who
/// carries no <c>tenant_id</c> claim (Story 5's third scenario).
/// </remarks>
internal sealed record MeResponse
{
    public required bool IsAuthenticated { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Sub { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Email { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TenantId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Roles { get; init; }

    internal static readonly MeResponse Anonymous = new() { IsAuthenticated = false };
}
