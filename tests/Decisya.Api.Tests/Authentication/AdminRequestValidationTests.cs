using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Decisya.Modules.Entitlements.Contracts;
using Decisya.Modules.Entitlements.Contracts.Admin;
using Decisya.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using NodaTime;

namespace Decisya.Api.Tests.Authentication;

/// <summary>
/// Issue #25 (G2 G5 list), no Docker: the real Api host with a stub <see cref="IEntitlementAdminCommands"/> (so
/// no database is needed). The 400 table for the path and the body, each <see cref="EntitlementAdminStatus"/>
/// mapped to its HTTP status, and the canary over every response, log record, activity tag and metric tag.
/// </summary>
[Trait("Category", "Unit")]
public sealed class AdminRequestValidationTests : IDisposable
{
    private const string Tenant = "7c9e6679-7425-40de-944b-e07fc1f90ae7";
    private const string Feature = "forecasting.scenarios";

    private readonly TestTokenIssuer _issuer = new();

    public void Dispose()
    {
        _issuer.Dispose();
        GC.SuppressFinalize(this);
    }

    public static TheoryData<string, string, string?, string> BadRequests => new()
    {
        { "POST", "/api/admin/tenants/not-a-guid/trial", null, "entitlements.tenant_invalid" },
        { "POST", "/api/admin/tenants/00000000-0000-0000-0000-000000000000/trial", null, "entitlements.tenant_invalid" },
        { "PUT", $"/api/admin/tenants/not-a-guid/overrides/{Feature}", """{"reason":"p"}""", "entitlements.tenant_invalid" },
        { "DELETE", $"/api/admin/tenants/not-a-guid/overrides/{Feature}", null, "entitlements.tenant_invalid" },
        { "PUT", $"/api/admin/tenants/{Tenant}/overrides/Forecasting.Scenarios", """{"reason":"p"}""", "entitlements.feature_unknown" },
        { "DELETE", $"/api/admin/tenants/{Tenant}/overrides/forecasting.scenarios%20", null, "entitlements.feature_unknown" },
        { "PUT", $"/api/admin/tenants/{Tenant}/overrides/{Feature}", "", "" },
        { "PUT", $"/api/admin/tenants/{Tenant}/overrides/{Feature}", "{\"reason\":\"p\"", "" },
        { "PUT", $"/api/admin/tenants/{Tenant}/overrides/{Feature}", "[\"p\"]", "" },
        { "PUT", $"/api/admin/tenants/{Tenant}/overrides/{Feature}", """{"reason":42}""", "" },
        { "PUT", $"/api/admin/tenants/{Tenant}/overrides/{Feature}", """{"reason":"p","expiresAt":"not-a-date"}""", "" },
        { "PUT", $"/api/admin/tenants/{Tenant}/overrides/{Feature}", """{"reason":"p","expiresAt":"2030-12-31T23:59:59"}""", "" },
        { "PUT", $"/api/admin/tenants/{Tenant}/overrides/{Feature}", """{"reason":"p","tenantId":"2f1a8d5e-3b4c-4e6f-9a7b-1c2d3e4f5a6b"}""", "" },
    };

    [Theory]
    [MemberData(nameof(BadRequests))]
    public async Task A_bad_path_or_body_gets_400_with_a_stable_code_or_a_generic_body_before_any_command_runs(
        string method, string path, string? body, string expectedCode)
    {
        var stub = new StubCommands();
        await using var factory = CreateFactory(stub);
        using var client = factory.CreateClient();

        using var response = await SendAsync(client, method, path, body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var members = json.RootElement.EnumerateObject().Select(p => p.Name).ToList();
        members.Should().NotContain(["detail", "instance"]);
        (json.RootElement.TryGetProperty("code", out var code) ? code.GetString() : string.Empty).Should().Be(expectedCode);
        stub.Calls.Should().Be(0);
    }

    [Fact]
    public async Task A_Content_Type_other_than_application_json_gets_400_before_any_command_runs()
    {
        var stub = new StubCommands();
        await using var factory = CreateFactory(stub);
        using var client = factory.CreateClient();

        using var response = await SendAsync(client, "PUT", $"/api/admin/tenants/{Tenant}/overrides/{Feature}", """{"reason":"p"}""", contentType: "text/plain");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        stub.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData(EntitlementAdminStatus.Forbidden, "entitlements.forbidden", 403, false)]
    [InlineData(EntitlementAdminStatus.Forbidden, "entitlements.actor_unknown", 403, false)]
    [InlineData(EntitlementAdminStatus.Invalid, "entitlements.reason_invalid", 400, true)]
    [InlineData(EntitlementAdminStatus.NotFound, "entitlements.tenant_not_found", 404, true)]
    [InlineData(EntitlementAdminStatus.Conflict, "entitlements.trial_already_used", 409, true)]
    public async Task Each_failure_status_of_the_facade_is_mapped_through_the_real_host(EntitlementAdminStatus status, string code, int expected, bool hasCode)
    {
        var stub = new StubCommands { Result = EntitlementAdminResult.Failed(status, code) };
        await using var factory = CreateFactory(stub);
        using var client = factory.CreateClient();

        using var response = await SendAsync(client, "POST", $"/api/admin/tenants/{Tenant}/trial");

        ((int)response.StatusCode).Should().Be(expected);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        json.RootElement.TryGetProperty("code", out var actual).Should().Be(hasCode);
        if (hasCode)
        {
            actual.GetString().Should().Be(code);
        }
    }

    [Fact]
    public async Task A_success_is_204_with_no_body_and_a_facade_that_throws_is_a_generic_500()
    {
        var stub = new StubCommands();
        await using var factory = CreateFactory(stub);
        using var client = factory.CreateClient();

        using var ok = await SendAsync(client, "POST", $"/api/admin/tenants/{Tenant}/trial");
        ok.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await ok.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().BeEmpty();

        stub.Fault = new InvalidOperationException("The entitlement admin facade returned an undefined status.");
        using var failed = await SendAsync(client, "POST", $"/api/admin/tenants/{Tenant}/trial");
        failed.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        failed.Headers.CacheControl!.NoStore.Should().BeTrue();
        var text = await failed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        text.Should().NotContain("InvalidOperationException").And.NotContain("undefined status");
    }

    [Fact]
    public async Task The_reason_is_never_echoed_or_logged_in_a_response_a_log_line_a_metric_tag_or_an_activity_tag()
    {
        const string email = "anna.meier@example.com";
        const string iban = "DE89370400440532013000";
        var reason = $"MARKER-9f3a-{Guid.NewGuid():N} Contact: {email}, IBAN {iban}";
        string[] canaries = ["MARKER-9f3a", email, iban];
        var stub = new StubCommands();
        var logs = new CapturingLoggerProvider();
        using var telemetry = new Telemetry();
        await using var factory = CreateFactory(stub, builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddFilter<CapturingLoggerProvider>(null, LogLevel.Trace);
            builder.AddProvider(logs);
        });
        using var client = factory.CreateClient();
        var seen = new List<string>();
        var path = $"/api/admin/tenants/{Tenant}/overrides/{Feature}";

        async Task SendAndCollectAsync(string url, string body, string contentType = "application/json")
        {
            using var response = await SendAsync(client, "PUT", url, body, contentType);
            seen.Add(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            seen.AddRange(response.Headers.Select(h => $"{h.Key}: {string.Join(',', h.Value)}"));
        }

        string Json(string tail) => "{\"reason\":\"" + reason + "\"" + tail + "}";

        await SendAndCollectAsync(path, Json(string.Empty));
        await SendAndCollectAsync(path, Json(",\"expiresAt\":\"" + reason + "\""));
        await SendAndCollectAsync(path, Json(",\"unknown\":\"" + reason + "\""));
        await SendAndCollectAsync(path, Json(",\"reason\":\"" + reason + "\""));
        await SendAndCollectAsync(path, "{\"reason\":\"" + reason);
        await SendAndCollectAsync(path, Json(string.Empty), "text/plain");
        await SendAndCollectAsync($"/api/admin/tenants/not-a-guid/overrides/{Feature}", Json(string.Empty));
        await SendAndCollectAsync($"/api/admin/tenants/{Tenant}/overrides/Bad%20Key", Json(string.Empty));
        stub.Result = EntitlementAdminResult.Failed(EntitlementAdminStatus.NotFound, EntitlementAdminErrorCodes.TenantNotFound);
        await SendAndCollectAsync(path, Json(string.Empty));
        stub.Fault = new InvalidOperationException("The entitlement admin facade returned an undefined status.");
        await SendAndCollectAsync(path, Json(string.Empty));

        logs.Records.Should().NotBeEmpty();
        telemetry.Values.Should().Contain("decisya.admin.outcome=succeeded");
        foreach (var canary in canaries)
        {
            seen.Should().NotContain(s => s.Contains(canary, StringComparison.Ordinal), $"no response may contain {canary}");
            logs.Records.Should().NotContain(r => r.Contains(canary), $"no log record at Trace may contain {canary}");
            telemetry.Values.Should().NotContain(v => v.Contains(canary, StringComparison.Ordinal), $"no activity or metric tag may contain {canary}");
        }
    }

    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> CreateFactory(StubCommands stub, Action<ILoggingBuilder>? configureLogging = null) =>
        ApiTestFactory.Create(
            _issuer,
            environmentName: "Production",
            configureServices: services => services.AddScoped<IEntitlementAdminCommands>(_ => stub),
            configureLogging: configureLogging);

    private async Task<HttpResponseMessage> SendAsync(
        HttpClient client, string method, string path, string? body = null, string contentType = "application/json")
    {
        var claims = TestTokenIssuer.DefaultClaims(subject: "3f2d1c9a-8b7e-4d6c-a5f4-0e1d2c3b4a59", tenantId: null);
        claims["roles"] = new[] { "platform-admin" };
        claims["acr"] = "2"; // #121: the MFA proof Keycloak's step-up flow gives an admin after the OTP.
        var token = TestTokenIssuer.IssueToken(claims, _issuer.RsaSigningKey, SecurityAlgorithms.RsaSha256);

        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (!string.IsNullOrEmpty(body))
        {
            request.Content = new StringContent(body, Encoding.UTF8, contentType);
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private sealed class StubCommands : IEntitlementAdminCommands
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public EntitlementAdminResult Result { get; set; } = EntitlementAdminResult.Succeeded;

        public Exception? Fault { get; set; }

        public Task<EntitlementAdminResult> StartTrialAsync(TenantId tenantId, CancellationToken cancellationToken = default) => Record();

        public Task<EntitlementAdminResult> GrantOverrideAsync(
            TenantId tenantId, FeatureKey feature, string reason, Instant? expiresAt, CancellationToken cancellationToken = default) => Record();

        public Task<EntitlementAdminResult> RevokeOverrideAsync(
            TenantId tenantId, FeatureKey feature, CancellationToken cancellationToken = default) => Record();

        private Task<EntitlementAdminResult> Record()
        {
            Interlocked.Increment(ref _calls);
            return Fault is { } fault ? throw fault : Task.FromResult(Result);
        }
    }

    /// <summary>Every activity name and tag and every measurement tag, of any source or meter, as strings.</summary>
    private sealed class Telemetry : IDisposable
    {
        private readonly ConcurrentQueue<string> _values = new();
        private readonly ActivityListener _activities;
        private readonly MeterListener _meters;

        public Telemetry()
        {
            _activities = new ActivityListener
            {
                ShouldListenTo = _ => true,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity =>
                {
                    _values.Enqueue(activity.DisplayName);
                    foreach (var tag in activity.TagObjects)
                    {
                        _values.Enqueue($"{tag.Key}={tag.Value}");
                    }
                },
            };
            ActivitySource.AddActivityListener(_activities);

            _meters = new MeterListener { InstrumentPublished = (instrument, listener) => listener.EnableMeasurementEvents(instrument) };
            _meters.SetMeasurementEventCallback<long>((_, _, tags, _) => Record(tags));
            _meters.Start();
        }

        public IReadOnlyList<string> Values => [.. _values];

        public void Dispose()
        {
            _activities.Dispose();
            _meters.Dispose();
        }

        private void Record(ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            foreach (var tag in tags)
            {
                _values.Enqueue($"{tag.Key}={tag.Value}");
            }
        }
    }
}
