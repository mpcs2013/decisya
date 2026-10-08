using System.Net;
using static Decisya.Bff.Tests.RateLimiting.RateLimitRequests;

namespace Decisya.Bff.Tests.RateLimiting;

/// <summary>#122 G5, Story 5 canary row "a bearer token": a limited request that carries a bearer token leaks it nowhere. No Docker.</summary>
[Trait("Category", "Unit")]
public class BearerCanaryTests
{
    [Theory]
    [InlineData("/api/anything", "198.51.100.160")]
    [InlineData("/bff/login", "198.51.100.161")]
    public async Task A_bearer_token_on_a_limited_request_appears_in_no_log_record_body_or_header(string path, string peer)
    {
        var ct = TestContext.Current.CancellationToken;
        var canary = Canaries.Unique("bearer");
        var provider = new CapturingLoggerProvider();
        using var factory = RateLimitFactory.Create(Limits(login: 1, anonymous: 1), loggerProvider: provider);
        using var client = factory.CreateBffClient();

        HttpRequestMessage Request()
        {
            var request = Get(path, peer);
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + canary);
            return request;
        }

        using (var first = await client.SendAsync(Request(), ct))
        {
            first.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests);
        }

        using var limited = await client.SendAsync(Request(), ct);
        limited.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

        var body = await limited.Content.ReadAsStringAsync(ct);
        body.Should().NotContain(canary);
        string.Join("\n", limited.Headers.Select(h => h.Key + ": " + string.Join(",", h.Value)))
            .Should().NotContain(canary, "no response header echoes the token");
        provider.Records.Where(r => r.EventId == 1820).Should().ContainSingle("the rejection was logged once");
        provider.Records.Should().NotContain(r => r.Contains(canary), "no log record, of any category, holds the bearer token");
    }
}
