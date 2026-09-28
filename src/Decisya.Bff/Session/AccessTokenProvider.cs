using System.Globalization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using NodaTime;

namespace Decisya.Bff.Session;

/// <summary>
/// #19 G2: decides whether the current request's access token can be forwarded as is, needs a
/// refresh first, or the session must end. Never parses the JWT itself (NetArchTest rule);
/// freshness is judged purely from the ticket's own <c>expires_at</c> token value.
/// </summary>
internal sealed class AccessTokenProvider(
    RedisTicketStore ticketStore,
    RedisRefreshLock refreshLock,
    KeycloakTokenClient tokenClient,
    IClock clock,
    ILogger<AccessTokenProvider> logger)
{
    // NFR-24: 10% of the <= 300 s maximum access-token lifespan (ADR-0002/NFR-15).
    private static readonly Duration RefreshLeadTime = Duration.FromSeconds(30);

    // D3/G3: a real wall-clock bound alongside the IClock-based one, so a frozen FakeClock in
    // tests can never turn a stuck lock into a hung test.
    private static readonly TimeSpan LockWaitWallClockBound = TimeSpan.FromSeconds(10);
    private static readonly Duration LockWaitDeadline = Duration.FromSeconds(10);
    private static readonly TimeSpan LockPollInterval = TimeSpan.FromMilliseconds(100);

    public async Task<AccessTokenResult> GetAsync(HttpContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var authenticateResult = await context.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme)
            .ConfigureAwait(false);
        if (!authenticateResult.Succeeded || authenticateResult.Ticket is null)
        {
            // UseAuthorization already required an authenticated caller before this ever runs;
            // this is a defensive fallback only.
            return AccessTokenResult.SessionEnded;
        }

        if (IsFresh(authenticateResult.Ticket, out var currentAccessToken))
        {
            return AccessTokenResult.Token(currentAccessToken!);
        }

        var sessionKey = context.Features.Get<SessionKeyFeature>()?.Key
            ?? throw new InvalidOperationException(
                "No SessionKeyFeature on the request; RedisTicketStore.RetrieveAsync(key, HttpContext, CancellationToken) must set it before AccessTokenProvider runs.");

        using var wallClockDeadline = new CancellationTokenSource(LockWaitWallClockBound);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, wallClockDeadline.Token);
        var deadlineInstant = clock.GetCurrentInstant() + LockWaitDeadline;

        while (true)
        {
            if (linkedSource.IsCancellationRequested || clock.GetCurrentInstant() >= deadlineInstant)
            {
                RecordRefreshTelemetry("lock_timeout");
                return AccessTokenResult.Unavailable;
            }

            IAsyncDisposable? lockHandle;
            try
            {
                lockHandle = await refreshLock.TryAcquireAsync(sessionKey, linkedSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                RecordRefreshTelemetry("lock_timeout");
                return AccessTokenResult.Unavailable;
            }

            if (lockHandle is null)
            {
                // Someone else holds the lock: wait, then see whether they already refreshed it.
                try
                {
                    await Task.Delay(LockPollInterval, linkedSource.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    RecordRefreshTelemetry("lock_timeout");
                    return AccessTokenResult.Unavailable;
                }

                var waitingTicket = await ticketStore.RetrieveAsync(sessionKey).ConfigureAwait(false);
                if (waitingTicket is null)
                {
                    // The holder hit B-1: every waiter gets 401 too.
                    return AccessTokenResult.SessionEnded;
                }

                if (IsFresh(waitingTicket, out var waitingAccessToken))
                {
                    return AccessTokenResult.Token(waitingAccessToken!);
                }

                continue;
            }

            await using (lockHandle.ConfigureAwait(false))
            {
                return await RefreshUnderLockAsync(sessionKey, linkedSource.Token).ConfigureAwait(false);
            }
        }
    }

    private async Task<AccessTokenResult> RefreshUnderLockAsync(string sessionKey, CancellationToken cancellationToken)
    {
        var reReadTicket = await ticketStore.RetrieveAsync(sessionKey).ConfigureAwait(false);
        if (reReadTicket is null)
        {
            return AccessTokenResult.SessionEnded;
        }

        if (IsFresh(reReadTicket, out var reReadAccessToken))
        {
            // Another request or BFF instance already refreshed it while we waited for the lock.
            return AccessTokenResult.Token(reReadAccessToken!);
        }

        var refreshToken = reReadTicket.Properties.GetTokenValue("refresh_token");
        if (string.IsNullOrEmpty(refreshToken))
        {
            return AccessTokenResult.SessionEnded;
        }

        var refreshResult = await tokenClient.RefreshAsync(refreshToken, cancellationToken).ConfigureAwait(false);

        switch (refreshResult.Outcome)
        {
            case TokenRefreshOutcome.Success:
                ApplyRefreshedTokens(reReadTicket, refreshResult);
                var updated = await ticketStore.TryUpdateTokensAsync(sessionKey, reReadTicket).ConfigureAwait(false);
                if (!updated)
                {
                    // T-09: a logout raced this refresh and deleted the ticket first.
                    RecordRefreshTelemetry("success");
                    return AccessTokenResult.SessionEnded;
                }

                RecordRefreshTelemetry("success");
                return AccessTokenResult.Token(refreshResult.AccessToken!);

            case TokenRefreshOutcome.InvalidGrant:
                return await HandleInvalidGrantAsync(sessionKey, refreshToken).ConfigureAwait(false);

            default:
                RecordRefreshTelemetry("error");
                return AccessTokenResult.Unavailable;
        }
    }

    /// <summary>S-3 (T-11): the lock's TTL can expire while a holder is still mid-refresh; a
    /// second holder then refreshes with the token the first holder already rotated away, and
    /// Keycloak answers <c>invalid_grant</c> for the second holder even though the session is
    /// fine. Re-reading the ticket before ending the session tells the two cases apart.</summary>
    private async Task<AccessTokenResult> HandleInvalidGrantAsync(string sessionKey, string refreshTokenJustSent)
    {
        var raceTicket = await ticketStore.RetrieveAsync(sessionKey).ConfigureAwait(false);
        var raceRefreshToken = raceTicket?.Properties.GetTokenValue("refresh_token");

        if (raceTicket is not null
            && !string.Equals(raceRefreshToken, refreshTokenJustSent, StringComparison.Ordinal)
            && IsFresh(raceTicket, out var raceAccessToken))
        {
            RecordRefreshTelemetry("invalid_grant");
            return AccessTokenResult.Token(raceAccessToken!);
        }

        RecordRefreshTelemetry("invalid_grant");
        return AccessTokenResult.SessionEnded;
    }

    private void ApplyRefreshedTokens(AuthenticationTicket ticket, TokenRefreshResult result)
    {
        var expiresAt = clock.GetCurrentInstant().ToDateTimeOffset() + TimeSpan.FromSeconds(result.ExpiresInSeconds);

        ticket.Properties.UpdateTokenValue("access_token", result.AccessToken!);
        ticket.Properties.UpdateTokenValue("refresh_token", result.RefreshToken!);
        ticket.Properties.UpdateTokenValue("expires_at", expiresAt.ToString("o", CultureInfo.InvariantCulture));

        if (!string.IsNullOrEmpty(result.IdToken))
        {
            ticket.Properties.UpdateTokenValue("id_token", result.IdToken);
        }
    }

    private bool IsFresh(AuthenticationTicket ticket, out string? accessToken)
    {
        accessToken = ticket.Properties.GetTokenValue("access_token");
        var expiresAtRaw = ticket.Properties.GetTokenValue("expires_at");

        if (string.IsNullOrEmpty(accessToken) || string.IsNullOrEmpty(expiresAtRaw)
            || !DateTimeOffset.TryParse(expiresAtRaw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var expiresAt))
        {
            return false;
        }

        var now = clock.GetCurrentInstant().ToDateTimeOffset();
        return Duration.FromTimeSpan(expiresAt - now) > RefreshLeadTime;
    }

    private void RecordRefreshTelemetry(string result)
    {
        if (result == "lock_timeout")
        {
            BffLog.RefreshLockTimedOut(logger);
        }

        BffTelemetry.TokenRefreshes.Add(1, new KeyValuePair<string, object?>("result", result));
    }
}
