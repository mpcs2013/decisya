using System.Threading.RateLimiting;
using NodaTime;

namespace Decisya.Bff.RateLimiting;

/// <summary>Counts the partition limiters that exist right now (the eviction test reads this).</summary>
internal sealed class RateLimitPartitionStats
{
    private int _tracked;

    internal int TrackedPartitions => Volatile.Read(ref _tracked);

    internal void Created() => Interlocked.Increment(ref _tracked);

    internal void Disposed() => Interlocked.Decrement(ref _tracked);
}

/// <summary>
/// What a refused lease tells the 429 writer: which bucket refused, and a claim on the "log once per window"
/// slot. The slot is claimed by the writer, not at acquire time, because the framework middleware can try
/// a request twice (a non-blocking attempt, then an async one) before it writes the 429.
/// </summary>
internal sealed record RateLimitRejection(RouteClass Class, PartitionKind Kind, Func<bool> ClaimLog);

/// <summary>
/// The per-partition limiter (#122, G2 D4, G3 G4-122-01 c). It derives from
/// <see cref="ReplenishingRateLimiter"/> and forwards <see cref="TryReplenish"/> and
/// <see cref="IdleDuration"/> to the inner sliding window, so the partitioned limiter's single
/// heartbeat both replenishes it (a partition recovers after its window) and evicts it once idle (state
/// stays bounded). A plain wrapper would never recover and never be evicted. It overrides
/// <see cref="DisposeAsyncCore"/> because the eviction path calls <c>DisposeAsync</c>, whose base does
/// nothing (spike R3).
/// </summary>
/// <remarks>
/// Spike R1: a rejected sliding-window lease carries no <c>RetryAfter</c>, so a refusal always gets the
/// computed value <c>ceil(window / segments)</c>, clamped to 1 and the window. The "log once per
/// partition per window" flag lives here, so it lives and dies with the partition and is never a
/// separate cache.
/// </remarks>
internal sealed class PartitionLimiter : ReplenishingRateLimiter
{
    internal static readonly MetadataName<RateLimitRejection> RejectionMetadata =
        MetadataName.Create<RateLimitRejection>("decisya.bff.ratelimit.rejection");

    private readonly SlidingWindowRateLimiter _inner;
    private readonly RouteClass _class;
    private readonly PartitionKind _kind;
    private readonly int _windowSeconds;
    private readonly TimeSpan _retryAfter;
    private readonly IClock _clock;
    private readonly RateLimitPartitionStats _stats;
    private readonly Lock _gate = new();
    private Instant _quietUntil = Instant.MinValue;
    private int _disposed;

    internal PartitionLimiter(
        RouteClass routeClass,
        PartitionKind kind,
        int permitLimit,
        int windowSeconds,
        int segments,
        IClock clock,
        RateLimitPartitionStats stats)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(stats);

        _class = routeClass;
        _kind = kind;
        _windowSeconds = windowSeconds;
        _retryAfter = TimeSpan.FromSeconds(ComputeRetryAfterSeconds(windowSeconds, segments));
        _clock = clock;
        _stats = stats;
        _inner = new SlidingWindowRateLimiter(new SlidingWindowRateLimiterOptions
        {
            PermitLimit = permitLimit,
            Window = TimeSpan.FromSeconds(windowSeconds),
            SegmentsPerWindow = segments,
            QueueLimit = 0,
            AutoReplenishment = false,
        });
        _stats.Created();
    }

    public override bool IsAutoReplenishing => false;

    public override TimeSpan ReplenishmentPeriod => _inner.ReplenishmentPeriod;

    public override TimeSpan? IdleDuration => _inner.IdleDuration;

    /// <summary><c>ceil(window / segments)</c>, the earliest moment any permit can return, clamped to [1, window].</summary>
    internal static int ComputeRetryAfterSeconds(int windowSeconds, int segments)
    {
        var seconds = (windowSeconds + segments - 1) / segments;
        return Math.Clamp(seconds, 1, Math.Max(1, windowSeconds));
    }

    public override bool TryReplenish() => _inner.TryReplenish();

    public override RateLimiterStatistics? GetStatistics() => _inner.GetStatistics();

    protected override RateLimitLease AttemptAcquireCore(int permitCount) => Decorate(_inner.AttemptAcquire(permitCount));

    protected override async ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken) =>
        Decorate(await _inner.AcquireAsync(permitCount, cancellationToken).ConfigureAwait(false));

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposeOnce();
        }

        base.Dispose(disposing);
    }

    protected override ValueTask DisposeAsyncCore()
    {
        DisposeOnce();
        return base.DisposeAsyncCore();
    }

    private void DisposeOnce()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _inner.Dispose();
            _stats.Disposed();
        }
    }

    private RateLimitLease Decorate(RateLimitLease lease)
    {
        if (lease.IsAcquired)
        {
            return lease;
        }

        return new RejectedLease(lease, new RateLimitRejection(_class, _kind, ClaimLogSlot), _retryAfter);
    }

    /// <summary>True for the first caller in each window of this partition; false for the rest of that window.</summary>
    private bool ClaimLogSlot()
    {
        var now = _clock.GetCurrentInstant();
        lock (_gate)
        {
            if (now < _quietUntil)
            {
                return false;
            }

            _quietUntil = now + Duration.FromSeconds(_windowSeconds);
            return true;
        }
    }

    private sealed class RejectedLease(RateLimitLease inner, RateLimitRejection rejection, TimeSpan retryAfter) : RateLimitLease
    {
        private static readonly string[] Names = [MetadataName.RetryAfter.Name, RejectionMetadata.Name];

        public override bool IsAcquired => false;

        public override IEnumerable<string> MetadataNames => Names;

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            if (string.Equals(metadataName, MetadataName.RetryAfter.Name, StringComparison.Ordinal))
            {
                metadata = retryAfter;
                return true;
            }

            if (string.Equals(metadataName, RejectionMetadata.Name, StringComparison.Ordinal))
            {
                metadata = rejection;
                return true;
            }

            metadata = null;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
