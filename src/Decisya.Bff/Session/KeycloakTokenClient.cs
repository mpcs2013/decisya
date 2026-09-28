using System.Net;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;

namespace Decisya.Bff.Session;

internal enum TokenRefreshOutcome
{
    Success,
    InvalidGrant,
    Error,
}

/// <summary>The outcome of <see cref="KeycloakTokenClient.RefreshAsync"/>. Never carries the
/// refresh token that went in; only the tokens that came back, for the caller to write onto
/// the ticket (CLAUDE.md: never log tokens).</summary>
internal sealed record TokenRefreshResult(
    TokenRefreshOutcome Outcome, string? AccessToken, string? RefreshToken, string? IdToken, int ExpiresInSeconds)
{
    internal static TokenRefreshResult Success(string accessToken, string refreshToken, string? idToken, int expiresInSeconds) =>
        new(TokenRefreshOutcome.Success, accessToken, refreshToken, idToken, expiresInSeconds);

    internal static readonly TokenRefreshResult InvalidGrant = new(TokenRefreshOutcome.InvalidGrant, null, null, null, 0);

    internal static readonly TokenRefreshResult Error = new(TokenRefreshOutcome.Error, null, null, null, 0);
}

/// <summary>
/// #19 G2: the refresh grant and the B-2 end-session call, both against Keycloak's own
/// discovered endpoints, over the OIDC handler's own <c>Backchannel</c>
/// (same TLS rules as login, no resilience retries — a retried POST under strict refresh-token
/// rotation would burn the token). Every value goes in the POST form body, never the query
/// string, and neither the refresh token nor the client secret is ever logged.
/// </summary>
internal sealed class KeycloakTokenClient(IOptionsMonitor<OpenIdConnectOptions> oidcOptionsMonitor, ILogger<KeycloakTokenClient> logger)
{
    private const string InvalidGrantMarker = "invalid_grant";

    // D3: below the 10 s refresh-lock TTL, so a slow Keycloak cannot outlive the lock that
    // guards the ticket write-back.
    private static readonly TimeSpan RefreshTimeout = TimeSpan.FromSeconds(5);

    // T-13: bounded so a Keycloak outage cannot make /bff/logout hang; the local logout
    // always completes regardless (G1 decision 2).
    private static readonly TimeSpan EndSessionTimeout = TimeSpan.FromSeconds(3);

    internal async Task<TokenRefreshResult> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(refreshToken);

        var oidcOptions = oidcOptionsMonitor.Get(OpenIdConnectDefaults.AuthenticationScheme);

        Uri tokenEndpoint;
        try
        {
            var configurationManager = oidcOptions.ConfigurationManager
                ?? throw new InvalidOperationException("The OIDC handler has no ConfigurationManager.");
            var configuration = await configurationManager.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
            tokenEndpoint = new Uri(configuration.TokenEndpoint);
        }
        catch (Exception exception) when (IsTransientCallFailure(exception))
        {
            BffLog.TokenRefreshFailed(logger, exception);
            return TokenRefreshResult.Error;
        }

        using var timeoutSource = new CancellationTokenSource(RefreshTimeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

        using var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["client_id"] = oidcOptions.ClientId!,
                ["client_secret"] = oidcOptions.ClientSecret!,
            }),
        };

        HttpResponseMessage response;
        try
        {
            response = await oidcOptions.Backchannel.SendAsync(request, linkedSource.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsTransientCallFailure(exception))
        {
            BffLog.TokenRefreshFailed(logger, exception);
            return TokenRefreshResult.Error;
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.OK)
            {
                return await ParseSuccessAsync(response, refreshToken, cancellationToken).ConfigureAwait(false);
            }

            if (response.StatusCode == HttpStatusCode.BadRequest
                && await BodyContainsAsync(response, InvalidGrantMarker, cancellationToken).ConfigureAwait(false))
            {
                BffLog.TokenRefreshInvalidGrant(logger);
                return TokenRefreshResult.InvalidGrant;
            }

            BffLog.TokenRefreshFailed(logger, new InvalidOperationException($"Unexpected status {(int)response.StatusCode}."));
            return TokenRefreshResult.Error;
        }
    }

    /// <summary>B-2: ends the whole Keycloak SSO session using the refresh token the BFF
    /// already holds. Fail-open (G1 decision 2): every failure is reported through the
    /// <see langword="bool"/> return only, never thrown, so the caller's local logout always
    /// completes.</summary>
    internal async Task<bool> EndSessionAsync(string refreshToken, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(refreshToken);

        var oidcOptions = oidcOptionsMonitor.Get(OpenIdConnectDefaults.AuthenticationScheme);

        Uri endSessionEndpoint;
        try
        {
            var configurationManager = oidcOptions.ConfigurationManager
                ?? throw new InvalidOperationException("The OIDC handler has no ConfigurationManager.");
            var configuration = await configurationManager.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
            endSessionEndpoint = new Uri(configuration.EndSessionEndpoint);
        }
        catch (Exception exception) when (IsTransientCallFailure(exception))
        {
            BffLog.KeycloakLogoutFailed(logger, exception);
            return false;
        }

        using var timeoutSource = new CancellationTokenSource(EndSessionTimeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

        using var request = new HttpRequestMessage(HttpMethod.Post, endSessionEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = oidcOptions.ClientId!,
                ["client_secret"] = oidcOptions.ClientSecret!,
                ["refresh_token"] = refreshToken,
            }),
        };

        try
        {
            using var response = await oidcOptions.Backchannel.SendAsync(request, linkedSource.Token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return true;
            }

            BffLog.KeycloakLogoutRejected(logger, (int)response.StatusCode);
            return false;
        }
        catch (Exception exception) when (IsTransientCallFailure(exception))
        {
            BffLog.KeycloakLogoutFailed(logger, exception);
            return false;
        }
    }

    private static async Task<TokenRefreshResult> ParseSuccessAsync(
        HttpResponseMessage response, string previousRefreshToken, CancellationToken cancellationToken)
    {
        TokenResponsePayload? payload;
        try
        {
            payload = await response.Content.ReadFromJsonAsync<TokenResponsePayload>(cancellationToken).ConfigureAwait(false);
        }
        catch (System.Text.Json.JsonException)
        {
            return TokenRefreshResult.Error;
        }

        if (payload is null || string.IsNullOrEmpty(payload.AccessToken) || payload.ExpiresIn <= 0)
        {
            return TokenRefreshResult.Error;
        }

        // Keycloak's strict rotation (refreshTokenMaxReuse: 0) always returns a new refresh
        // token, but the previous one is kept if a response ever omits it (G2).
        var rotatedRefreshToken = string.IsNullOrEmpty(payload.RefreshToken) ? previousRefreshToken : payload.RefreshToken;

        return TokenRefreshResult.Success(payload.AccessToken, rotatedRefreshToken, payload.IdToken, payload.ExpiresIn);
    }

    private static async Task<bool> BodyContainsAsync(HttpResponseMessage response, string marker, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return body.Contains(marker, StringComparison.Ordinal);
    }

    private static bool IsTransientCallFailure(Exception exception) =>
        exception is HttpRequestException or TaskCanceledException or OperationCanceledException or InvalidOperationException or UriFormatException;
}
