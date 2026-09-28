using NodaTime;
using StackExchange.Redis;

namespace Decisya.Bff.Session;

/// <summary>
/// #19 G2 (D3): the per-session refresh lock — exactly one refresh per session even under
/// concurrent requests (Story 3, NFR-25). Key <c>decisya:bff:refresh-lock:{sessionKey}</c>,
/// TTL 10 s (G3: worst-case holder time 1 s re-read + 5 s refresh + 1 s conditional write = 7 s,
/// below the TTL), release only if the value still matches
/// (<see cref="IDatabaseAsync.LockReleaseAsync"/> does this internally), so an expired-and-
/// retaken lock is never released by the wrong holder. Works across BFF instances because it
/// lives in the shared Redis; no in-process cache is added.
/// </summary>
internal sealed class RedisRefreshLock(IConnectionMultiplexer connectionMultiplexer, IClock clock)
{
    private const string LockKeyPrefix = "decisya:bff:refresh-lock:";
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    internal static readonly TimeSpan LockTtl = TimeSpan.FromSeconds(10);

    internal async Task<IAsyncDisposable?> TryAcquireAsync(string sessionKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionKey);

        var database = connectionMultiplexer.GetDatabase();
        var lockValue = Guid.NewGuid().ToString("N");
        var lockKey = LockKeyName(sessionKey);

        var acquired = await database.LockTakeAsync(lockKey, lockValue, LockTtl)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        return acquired ? new LockHandle(database, lockKey, lockValue) : null;
    }

    /// <summary>
    /// #19 G2 (S-4, T-14): <c>/bff/logout</c>'s own bounded wait for the lock — the same 10 s
    /// bound as the refresh state machine, so logout never sends a refresh token a concurrent
    /// refresh is about to rotate. Unlike <see cref="AccessTokenProvider"/>'s loop, this never
    /// re-reads the ticket between attempts: logout does not care whether the token is fresh,
    /// only that no refresh is in flight. Returns <see langword="null"/> on timeout (G1
    /// decision 2: the caller still completes the local logout, fail-open).
    /// </summary>
    internal async Task<IAsyncDisposable?> AcquireOrWaitAsync(string sessionKey, CancellationToken cancellationToken)
    {
        var deadline = clock.GetCurrentInstant() + Duration.FromTimeSpan(LockTtl);
        using var wallClockBound = new CancellationTokenSource(LockTtl);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, wallClockBound.Token);

        while (true)
        {
            IAsyncDisposable? handle;
            try
            {
                handle = await TryAcquireAsync(sessionKey, linkedSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }

            if (handle is not null)
            {
                return handle;
            }

            if (linkedSource.IsCancellationRequested || clock.GetCurrentInstant() >= deadline)
            {
                return null;
            }

            try
            {
                await Task.Delay(PollInterval, linkedSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }
    }

    private static string LockKeyName(string sessionKey) => LockKeyPrefix + sessionKey;

    private sealed class LockHandle(IDatabase database, RedisKey lockKey, RedisValue lockValue) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await database.LockReleaseAsync(lockKey, lockValue).ConfigureAwait(false);
        }
    }
}
