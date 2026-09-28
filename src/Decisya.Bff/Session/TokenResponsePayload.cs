using System.Text.Json.Serialization;

namespace Decisya.Bff.Session;

/// <summary>The fields <see cref="KeycloakTokenClient"/> reads out of Keycloak's
/// <c>refresh_token</c> grant response. Never logged or forwarded whole (CLAUDE.md).</summary>
internal sealed class TokenResponsePayload
{
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; init; }

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; init; }

    [JsonPropertyName("id_token")]
    public string? IdToken { get; init; }

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; init; }
}
