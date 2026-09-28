namespace Decisya.Api.Authentication;

/// <summary>D3 (G2): the audience is a code constant, not configuration — one fewer value to
/// misconfigure. Only <c>decisya-bff</c> carries the realm's <c>audience-decisya-api</c>
/// mapper (docs/architecture/keycloak-realm.md).</summary>
internal static class ApiJwtDefaults
{
    internal const string Audience = "decisya-api";
}
