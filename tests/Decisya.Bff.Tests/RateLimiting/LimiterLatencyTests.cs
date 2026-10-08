using System.Diagnostics;
using System.Globalization;
using System.Net;
using static Decisya.Bff.Tests.RateLimiting.RateLimitRequests;

namespace Decisya.Bff.Tests.RateLimiting;

/// <summary>
/// #122 G5, NFR-53: the limiter adds little latency at 50 concurrent requests. In-process (TestServer, no
/// listener, no Docker). The "off" arm is the same host with no limiter evaluated (the middleware and the
/// partition step stay in the pipeline, so the arm isolates the limiter evaluation itself). Host noise is
/// handled by a warm-up, interleaved arms, several rounds, the median of the per-round p95 values, a
/// relative bound with a generous margin and an absolute floor. Both numbers are in the failure message.
/// Tagged Integration so the fast lane (and its noisy shared CI runners) never runs it.
/// </summary>
[Trait("Category", "Integration")]
public class LimiterLatencyTests
{
    private const int Concurrency = 50;
    private const int WarmUpRounds = 4;
    private const int MeasuredRounds = 9;

    // Relative bound with a generous margin, or the absolute floor in milliseconds, whichever is more lenient.
    private const double RelativeFactor = 2.0;
    private const double AbsoluteFloorMilliseconds = 10.0;

    private static string PeerFor(int round, int index) =>
        string.Create(CultureInfo.InvariantCulture, $"198.18.{(round % 200) + 1}.{index + 1}");

    private static async Task<double[]> RoundAsync(HttpClient client, int round)
    {
        var tasks = Enumerable.Range(0, Concurrency).Select(async index =>
        {
            using var request = Get("/api/anything", peer: PeerFor(round, index));
            var start = Stopwatch.GetTimestamp();
            using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
            var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the measured path is the anonymous api path, never a 429");
            return elapsed;
        });
        return await Task.WhenAll(tasks);
    }

    private static double P95(double[] values)
    {
        var sorted = values.Order().ToArray();
        return sorted[(int)Math.Ceiling(sorted.Length * 0.95) - 1];
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        return sorted[sorted.Length / 2];
    }

    [Fact]
    public async Task The_limiter_adds_little_p95_latency_at_50_concurrent_requests()
    {
        var limits = Limits(login: 100000, backchannel: 100000, api: 100000, anonymous: 100000, admin: 100000);
        using var on = RateLimitFactory.Create(limits);
        using var off = RateLimitFactory.Create(limits, limiterOff: true);
        using var onClient = on.CreateBffClient();
        using var offClient = off.CreateBffClient();

        for (var warm = 0; warm < WarmUpRounds; warm++)
        {
            await RoundAsync(onClient, warm);
            await RoundAsync(offClient, warm);
        }

        var onP95 = new List<double>();
        var offP95 = new List<double>();
        for (var round = 0; round < MeasuredRounds; round++)
        {
            // Alternate which arm goes first so drift (GC, tiering) does not favour either.
            if (round % 2 == 0)
            {
                onP95.Add(P95(await RoundAsync(onClient, 100 + round)));
                offP95.Add(P95(await RoundAsync(offClient, 100 + round)));
            }
            else
            {
                offP95.Add(P95(await RoundAsync(offClient, 100 + round)));
                onP95.Add(P95(await RoundAsync(onClient, 100 + round)));
            }
        }

        var on95 = Median(onP95);
        var off95 = Median(offP95);
        var allowed = Math.Max(off95 * RelativeFactor, off95 + AbsoluteFloorMilliseconds);
        var message = string.Create(
            CultureInfo.InvariantCulture,
            $"limiter on p95 = {on95:F2} ms, limiter off p95 = {off95:F2} ms, allowed = {allowed:F2} ms (median of {MeasuredRounds} rounds of {Concurrency} concurrent requests)");
        TestContext.Current.SendDiagnosticMessage("NFR-53 " + message);

        on95.Should().BeLessThanOrEqualTo(allowed, message);
    }
}
