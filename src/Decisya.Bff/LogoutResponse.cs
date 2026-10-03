namespace Decisya.Bff;

/// <summary>
/// The JSON variant of <c>POST /bff/logout</c> (#26 G2 D4): the Keycloak end-session URL the OIDC
/// handler built, as data, for a caller that sent <c>Accept: application/json</c>. Serializes to
/// exactly <c>{"redirectUri":"..."}</c>. Never carries a token (the handler sends no
/// <c>id_token_hint</c>).
/// </summary>
internal sealed record LogoutResponse(string RedirectUri);
