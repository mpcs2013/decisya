using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;

namespace Decisya.Bff.Tests;

/// <summary>
/// #27 B-01 (#15 T-20 / F-4): the BFF is the trust boundary, so it never adopts a browser-chosen
/// trace id, tracestate or baggage, and never forwards them to the Api or to Keycloak. Every
/// request starts a new trace root; the outbound calls carry the BFF's own context, so Api spans
/// still join the BFF's trace.
/// </summary>
[Trait("Category", "Integration")]
public partial class TraceContextIsolationTests
{
    private const string CanaryTraceId = "4bf92f3577b34da6a3ce929d0e0e4736";
    private const string CanarySpanId = "00f067aa0ba902b7";
    private const string CanaryTraceparent = "00-" + CanaryTraceId + "-" + CanarySpanId + "-01";
    private const string CanaryTracestate = "canary=vendor-state";
    private const string CanaryBaggage = "canary=browser-chosen-value";

    private readonly KeycloakBffFixture _keycloakFixture;
    private readonly RedisFixture _redisFixture;

    public TraceContextIsolationTests(KeycloakBffFixture keycloakFixture, RedisFixture redisFixture)
    {
        _keycloakFixture = keycloakFixture;
        _redisFixture = redisFixture;
    }

    [GeneratedRegex("^00-(?<trace>[0-9a-f]{32})-(?<span>[0-9a-f]{16})-0[01]$")]
    private static partial Regex TraceparentShape();

    [Fact]
    public async Task A_browser_chosen_trace_context_and_baggage_neither_root_the_bff_trace_nor_reach_the_api()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _keycloakFixture.EnsureStartedAsync(cancellationToken);
        await _redisFixture.EnsureStartedAsync(cancellationToken);

        await using var apiDouble = await ApiDouble.StartAsync(cancellationToken);
        using var factory = BffFactoryFactory.Create(_keycloakFixture, _redisFixture, apiAddress: apiDouble.Address);
        using var capture = new ServerActivityCapture();

        var jar = new OriginCookieJar();
        using var browser = new HttpClient(new OriginCookieHandler(jar, new CanaryHeaderHandler(factory.Server.CreateHandler())))
        {
            BaseAddress = new Uri("https://localhost:7200"),
        };
        await LoginFlowHarness.LogInWithClientAsync(
            browser, jar, _keycloakFixture, "dev-alice", _keycloakFixture.DevUserPassword, "/dashboard", cancellationToken);

        capture.Clear();

        using var response = await browser.GetAsync("/api/widgets/42", cancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Inbound: the BFF's server activity is a fresh root, not the browser's trace.
        // The ApiDouble runs in this process too; its own server activity is the "http" one.
        var bffActivity = capture.Snapshot().Should().ContainSingle(a => a.Path == "/api/widgets/42" && a.Scheme == "https").Subject;
        var serverActivity = bffActivity;
        serverActivity.TraceId.Should().NotBe(CanaryTraceId, "the BFF must not adopt a browser-chosen trace id");
        serverActivity.ParentSpanId.Should().Be(default(ActivitySpanId).ToHexString(), "the BFF starts a new trace root");
        serverActivity.BaggageKeys.Should().NotContain("canary", "browser baggage must not become BFF baggage");

        // Outbound: the Api sees the BFF's own context, never the browser's.
        var forwarded = apiDouble.Requests.Should().ContainSingle().Subject;
        forwarded.Headers.Should().ContainKey("traceparent");
        var traceparent = forwarded.Headers["traceparent"].Should().ContainSingle().Subject;
        var match = TraceparentShape().Match(traceparent);
        match.Success.Should().BeTrue("the Api receives a valid W3C traceparent");
        match.Groups["trace"].Value.Should().NotBe(CanaryTraceId);
        match.Groups["trace"].Value.Should().Be(serverActivity.TraceId, "the Api's spans join the BFF's trace");

        capture.Snapshot().Should().ContainSingle(a => a.Scheme == "http" && a.TraceId == serverActivity.TraceId, "the Api's own server span joins the BFF's trace");

        traceparent.Should().NotContain(CanaryTraceId);
        forwarded.Headers.Should().NotContainKey("baggage");
        if (forwarded.Headers.TryGetValue("tracestate", out var tracestate))
        {
            tracestate.Should().NotContain(value => value.Contains("canary", StringComparison.Ordinal));
        }

        forwarded.Headers.Values.SelectMany(values => values).Should()
            .NotContain(value => value.Contains(CanaryTraceId, StringComparison.Ordinal) || value.Contains("browser-chosen-value", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Every_request_of_the_login_flow_is_a_new_root_and_the_keycloak_backchannel_carries_only_the_bffs_context()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _keycloakFixture.EnsureStartedAsync(cancellationToken);
        await _redisFixture.EnsureStartedAsync(cancellationToken);

        using var recorder = new HeaderRecordingBackchannelHandler(new HttpClientHandler());
        using var factory = BffFactoryFactory.Create(_keycloakFixture, _redisFixture, backchannelHttpHandler: recorder);
        using var capture = new ServerActivityCapture();

        var jar = new OriginCookieJar();
        using var browser = new HttpClient(new OriginCookieHandler(jar, new CanaryHeaderHandler(factory.Server.CreateHandler())))
        {
            BaseAddress = new Uri("https://localhost:7200"),
        };
        await LoginFlowHarness.LogInWithClientAsync(
            browser, jar, _keycloakFixture, "dev-alice", _keycloakFixture.DevUserPassword, "/dashboard", cancellationToken);

        var activities = capture.Snapshot();
        activities.Should().NotBeEmpty();
        activities.Should().OnlyContain(
            activity => activity.TraceId != CanaryTraceId && activity.ParentSpanId == default(ActivitySpanId).ToHexString(),
            "every BFF request starts its own trace root");
        activities.Should().OnlyContain(activity => !activity.BaggageKeys.Contains("canary"));

        var tokenExchange = recorder.Sent.Should().ContainSingle(
            sent => sent.Method == "POST" && sent.Path.EndsWith("/protocol/openid-connect/token", StringComparison.Ordinal),
            "the callback exchanges the code once").Subject;
        tokenExchange.Headers.Should().ContainKey("traceparent");
        var match = TraceparentShape().Match(tokenExchange.Headers["traceparent"].Single());
        match.Success.Should().BeTrue();
        match.Groups["trace"].Value.Should().NotBe(CanaryTraceId);
        activities.Select(activity => activity.TraceId).Should().Contain(
            match.Groups["trace"].Value, "the Keycloak call belongs to a BFF-rooted trace");

        recorder.Sent.Should().NotBeEmpty();
        foreach (var sent in recorder.Sent)
        {
            sent.Headers.Should().NotContainKey("baggage");
            sent.Headers.Values.SelectMany(values => values).Should().NotContain(
                value => value.Contains(CanaryTraceId, StringComparison.Ordinal) || value.Contains("canary", StringComparison.Ordinal),
                $"{sent.Method} {sent.Path} must not carry browser trace context");
        }
    }

    /// <summary>Adds the canary trace context and baggage to every request, like a hostile or
    /// instrumented browser would.</summary>
    private sealed class CanaryHeaderHandler(HttpMessageHandler innerHandler) : DelegatingHandler(innerHandler)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.TryAddWithoutValidation("traceparent", CanaryTraceparent);
            request.Headers.TryAddWithoutValidation("tracestate", CanaryTracestate);
            request.Headers.TryAddWithoutValidation("baggage", CanaryBaggage);
            return base.SendAsync(request, cancellationToken);
        }
    }

    /// <summary>Wraps the real backchannel handler and snapshots the request headers after the
    /// send: the runtime injects <c>traceparent</c> inside the inner handler, onto the same
    /// request instance.</summary>
    private sealed class HeaderRecordingBackchannelHandler(HttpMessageHandler innerHandler) : DelegatingHandler(innerHandler)
    {
        internal ConcurrentQueue<SentRequest> Sent { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            try
            {
                return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Sent.Enqueue(new SentRequest(
                    request.Method.Method,
                    request.RequestUri?.AbsolutePath ?? string.Empty,
                    request.Headers.ToDictionary(
                        header => header.Key, header => header.Value.ToArray(), StringComparer.OrdinalIgnoreCase)));
            }
        }
    }

    private sealed record SentRequest(string Method, string Path, IReadOnlyDictionary<string, string[]> Headers);

    private sealed record ServerActivity(string Path, string Scheme, string TraceId, string ParentSpanId, IReadOnlyList<string> BaggageKeys);

    /// <summary>Captures the ASP.NET Core server activity of every request the in-process BFF
    /// handles. Listeners are process-wide; the assembly runs its tests serially.</summary>
    private sealed class ServerActivityCapture : IDisposable
    {
        private readonly ConcurrentQueue<ServerActivity> _activities = new();
        private readonly ActivityListener _listener;

        internal ServerActivityCapture()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == "Microsoft.AspNetCore",
                Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity =>
                {
                    if (activity.OperationName != "Microsoft.AspNetCore.Hosting.HttpRequestIn")
                    {
                        return;
                    }

                    var path = activity.GetTagItem("url.path") as string ?? string.Empty;
                    _activities.Enqueue(new ServerActivity(
                        path,
                        activity.GetTagItem("url.scheme") as string ?? string.Empty,
                        activity.TraceId.ToHexString(),
                        activity.ParentSpanId.ToHexString(),
                        activity.Baggage.Select(pair => pair.Key).ToArray()));
                },
            };
            ActivitySource.AddActivityListener(_listener);
        }

        internal void Clear() => _activities.Clear();

        internal ServerActivity[] Snapshot() => _activities.ToArray();

        public void Dispose() => _listener.Dispose();
    }
}
