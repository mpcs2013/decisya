namespace Decisya.Api.Capabilities;

/// <summary>
/// <c>GET /api/capabilities</c>'s response shape (issue #26, ADR-0008 amendment 1): exactly the
/// keys of <c>FeatureKeys.All</c>, each mapped to <c>true</c> or <c>false</c>. A host wire DTO like
/// <c>WhoAmIResponse</c>, not a module Contract. Keys are serialized as-is.
/// </summary>
public sealed record CapabilitiesResponse
{
    public required IReadOnlyDictionary<string, bool> Capabilities { get; init; }
}
