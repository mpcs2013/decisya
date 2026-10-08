using System.Globalization;
using System.Threading.RateLimiting;
using Decisya.Bff.Session;
using Decisya.ServiceDefaults.Logging;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Decisya.Bff.RateLimiting;

/// <summary>
/// The #122 log event (G2 D6, G1 D2): a fixed template with two arguments from closed lists.
/// <c>tenant_id</c> and the hashed <c>user_id</c> are never arguments; they arrive through
/// <see cref="ILogEnrichmentContext"/> for the one call, as in #121. Nothing request-derived is a parameter.
/// </summary>
internal static partial class RateLimitLog
{
    [LoggerMessage(
        EventId = 1820,
        EventName = "ratelimit.rejected",
        Level = LogLevel.Warning,
        Message = "ratelimit.rejected: a request was refused by the rate limiter ({RouteClass}, {PartitionKind}).")]
    internal static partial void Rejected(ILogger logger, string routeClass, string partitionKind);
}

/// <summary>
/// The 429 (G2 D6, G1 D2): <c>application/problem+json</c> with <c>type</c>, <c>title</c>, <c>status</c> and
/// <c>traceId</c> and nothing else, a whole-second <c>Retry-After</c> and <c>no-store</c>. No limit, class,
/// partition key, address or session appears in the body or the headers. No cookie is written: the
/// endpoint never runs, and the session cookie is absolute, not sliding.
/// </summary>
internal static class RateLimitResponses
{
    internal const string ProblemType = "https://tools.ietf.org/html/rfc6585#section-4";
    internal const string ProblemTitle = "Too many requests";

    private const string LoggerCategory = "Decisya.Bff.RateLimiting";

    internal static async ValueTask WriteAsync(OnRejectedContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var http = context.HttpContext;
        var feature = http.Features.Get<RateLimitPartitionFeature>();
        var rejection = context.Lease.TryGetMetadata(PartitionLimiter.RejectionMetadata, out var found) ? found : null;

        var routeClass = rejection?.Class ?? feature?.Class ?? RouteClass.Api;
        var kind = rejection?.Kind ?? feature?.Kind ?? PartitionKind.Unknown;

        RecordRejection(http, feature, routeClass, kind, rejection?.ClaimLog() ?? true);

        http.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        http.Response.Headers.RetryAfter = RetryAfterSeconds(context, routeClass).ToString(CultureInfo.InvariantCulture);
        http.Response.Headers.CacheControl = "no-store";

        var traceId = System.Diagnostics.Activity.Current?.Id ?? http.TraceIdentifier;
        await Results.Problem(
                statusCode: StatusCodes.Status429TooManyRequests,
                title: ProblemTitle,
                type: ProblemType,
                extensions: new Dictionary<string, object?> { ["traceId"] = traceId })
            .ExecuteAsync(http)
            .ConfigureAwait(false);
    }

    private static int RetryAfterSeconds(OnRejectedContext context, RouteClass routeClass)
    {
        var options = context.HttpContext.RequestServices.GetRequiredService<IOptions<BffRateLimitOptions>>().Value;
        var limit = options.For(routeClass);
        var window = Math.Max(1, limit.WindowSeconds);

        // The refusing bucket's computed value (spike R1: a sliding-window lease never supplies one).
        var seconds = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
            ? (int)Math.Ceiling(retryAfter.TotalSeconds)
            : PartitionLimiter.ComputeRetryAfterSeconds(limit.WindowSeconds, BffRateLimitOptions.SegmentsFor(routeClass));

        return Math.Clamp(seconds, 1, window);
    }

    private static void RecordRejection(
        HttpContext http, RateLimitPartitionFeature? feature, RouteClass routeClass, PartitionKind kind, bool logIt)
    {
        var className = RateLimitNames.Of(routeClass);
        BffTelemetry.RateLimitRejected.Add(1, new KeyValuePair<string, object?>("route_class", className));

        if (!logIt)
        {
            return;
        }

        var logger = http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(LoggerCategory);
        var kindName = RateLimitNames.Of(kind);
        var principal = feature?.Principal;
        if (principal is null)
        {
            RateLimitLog.Rejected(logger, className, kindName);
            return;
        }

        // HttpContext.User is still anonymous here (the limiter runs before authentication), so the scope
        // is opened from the principal the partition step captured, for this one call.
        var enrichment = http.RequestServices.GetRequiredService<ILogEnrichmentContext>();
        using (enrichment.Begin(AuthEvents.TenantOf(principal), AuthEvents.SubjectOf(principal)))
        {
            RateLimitLog.Rejected(logger, className, kindName);
        }
    }
}
