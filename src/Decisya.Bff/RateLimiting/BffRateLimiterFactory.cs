using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;
using NodaTime;

namespace Decisya.Bff.RateLimiting;

/// <summary>
/// What the partition step found out about a request (G2 D1): its route class, what it is counted
/// under, and the principal for log enrichment only. Never an authorization input.
/// </summary>
internal sealed record RateLimitPartitionFeature(
    RouteClass Class, PartitionKind Kind, string Value, System.Security.Claims.ClaimsPrincipal? Principal);

/// <summary>
/// Builds the global chained limiter (G2 D4): the class bucket first (<c>login</c>,
/// <c>backchannel_logout</c> and <c>admin</c>), then the api bucket (every <c>api</c> and
/// <c>admin</c> request). An admin request is therefore counted in both, and the tighter limit decides.
/// A request with no class gets no limiter.
/// </summary>
internal static class BffRateLimiterFactory
{
    internal static PartitionedRateLimiter<HttpContext> Create(
        BffRateLimitOptions options, IClock clock, RateLimitPartitionStats stats)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(stats);

        var classLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        {
            var feature = context.Features.Get<RateLimitPartitionFeature>();
            if (feature is null || feature.Class == RouteClass.Api)
            {
                return RateLimitPartition.GetNoLimiter("none");
            }

            var limit = options.For(feature.Class);
            var segments = BffRateLimitOptions.SegmentsFor(feature.Class);
            return RateLimitPartition.Get(
                Key(feature.Class, feature),
                _ => new PartitionLimiter(feature.Class, feature.Kind, limit.PermitLimit, limit.WindowSeconds, segments, clock, stats));
        });

        var apiLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        {
            var feature = context.Features.Get<RateLimitPartitionFeature>();
            if (feature is null || feature.Class is not (RouteClass.Api or RouteClass.Admin))
            {
                return RateLimitPartition.GetNoLimiter("none");
            }

            var limit = options.Api;
            var permits = feature.Kind == PartitionKind.Session ? limit.PermitLimit : limit.AnonymousPermitLimit;
            var segments = BffRateLimitOptions.SegmentsFor(RouteClass.Api);
            return RateLimitPartition.Get(
                Key(RouteClass.Api, feature),
                _ => new PartitionLimiter(RouteClass.Api, feature.Kind, permits, limit.WindowSeconds, segments, clock, stats));
        });

        return PartitionedRateLimiter.CreateChained(classLimiter, apiLimiter);
    }

    private static string Key(RouteClass bucketClass, RateLimitPartitionFeature feature) =>
        RateLimitNames.Of(bucketClass) + "|" + RateLimitNames.Of(feature.Kind) + "|" + feature.Value;
}

/// <summary>Owns the global limiter for the life of the host (and disposes it at shutdown).</summary>
internal sealed class BffRateLimiterHolder(
    IOptions<BffRateLimitOptions> options, IClock clock, RateLimitPartitionStats stats) : IDisposable
{
    internal PartitionedRateLimiter<HttpContext> Limiter { get; } = BffRateLimiterFactory.Create(options.Value, clock, stats);

    public void Dispose() => Limiter.Dispose();
}
