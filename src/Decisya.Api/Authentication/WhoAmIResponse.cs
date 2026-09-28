using System.Text.Json.Serialization;

namespace Decisya.Api.Authentication;

/// <summary>
/// <c>GET /api/whoami</c>'s response shape (D1). <see cref="TenantId"/> is left out of the
/// JSON entirely when absent: never <c>""</c>, and never the all-zero GUID (Story 1).
/// </summary>
public sealed record WhoAmIResponse
{
    public required string UserId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TenantId { get; init; }
}
