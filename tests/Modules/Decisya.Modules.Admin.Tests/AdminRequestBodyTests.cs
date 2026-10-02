using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Decisya.Modules.Admin.Tests.TestSupport;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Decisya.Modules.Admin.Tests;

/// <summary>
/// G3 G4-25-04 (T-10, T-11): the PUT body is read explicitly after authorization, within 8 KiB (including
/// the missing-Content-Length fallback and chunked bodies), through a strict serializer; parse errors are not
/// logged; and the reason never appears in a response, a log record at Trace, an activity tag or a metric tag.
/// </summary>
[Trait("Category", "Unit")]
public sealed class AdminRequestBodyTests
{
    private const string Tenant = "7c9e6679-7425-40de-944b-e07fc1f90ae7";
    private const string Path = $"/api/admin/tenants/{Tenant}/overrides/forecasting.scenarios";
    private const int Limit = 8192;

    public static TheoryData<string> BodyLimitModes => new() { nameof(BodyLimitMode.Writable), nameof(BodyLimitMode.ReadOnly), nameof(BodyLimitMode.Absent) };

    // Schema failures: every row is a 400 with a generic ProblemDetails (no code), before any command runs.
    public static TheoryData<string, string, string> RejectedBodies => new()
    {
        { "no body", "", "application/json" },
        { "invalid JSON", "{\"reason\":\"Pilot\"", "application/json" },
        { "a JSON array", "[\"Pilot\"]", "application/json" },
        { "a JSON string", "\"Pilot\"", "application/json" },
        { "a number for reason", "{\"reason\":42}", "application/json" },
        { "a null reason", "{\"reason\":null}", "application/json" },
        { "a missing reason", "{\"expiresAt\":\"2030-01-01T00:00:00Z\"}", "application/json" },
        { "an object for reason", "{\"reason\":{\"a\":\"b\"}}", "application/json" },
        { "an unknown member", "{\"reason\":\"Pilot\",\"extra\":1}", "application/json" },
        { "the tenant id in the body", "{\"reason\":\"Pilot\",\"tenantId\":\"2f1a8d5e-3b4c-4e6f-9a7b-1c2d3e4f5a6b\"}", "application/json" },
        { "a duplicate reason member", "{\"reason\":\"Pilot\",\"reason\":\"Other\"}", "application/json" },
        { "a duplicate expiresAt member", "{\"reason\":\"Pilot\",\"expiresAt\":null,\"expiresAt\":\"2030-01-01T00:00:00Z\"}", "application/json" },
        { "a number for expiresAt", "{\"reason\":\"Pilot\",\"expiresAt\":20300101}", "application/json" },
        { "expiresAt not a date", "{\"reason\":\"Pilot\",\"expiresAt\":\"not-a-date\"}", "application/json" },
        { "expiresAt without the UTC designator", "{\"reason\":\"Pilot\",\"expiresAt\":\"2030-12-31T23:59:59\"}", "application/json" },
        { "expiresAt with an offset", "{\"reason\":\"Pilot\",\"expiresAt\":\"2030-12-31T23:59:59+01:00\"}", "application/json" },
        { "a depth beyond the limit", "{\"reason\":\"Pilot\",\"a\":{\"b\":{\"c\":{\"d\":{\"e\":1}}}}}", "application/json" },
        { "a Content-Type of text/plain", "{\"reason\":\"Pilot\"}", "text/plain" },
        { "a Content-Type of application/x-www-form-urlencoded", "reason=Pilot", "application/x-www-form-urlencoded" },
    };

    private static Task<HttpResponseMessage> SendAsync(AdminTestHost host, HttpRequestMessage request) =>
        host.Client.SendAsync(request, TestContext.Current.CancellationToken);

    private static byte[] BodyOfSize(int bytes)
    {
        // {"reason":"aaa..."} is 13 bytes of framing plus the padding.
        var json = "{\"reason\":\"" + new string('a', bytes - 13) + "\"}";
        var data = Encoding.UTF8.GetBytes(json);
        data.Length.Should().Be(bytes);
        return data;
    }

    private static HttpRequestMessage Put(HttpContent content, string path = Path)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, path) { Content = content };
        request.Headers.Add(AdminTestHost.CallerHeader, nameof(TestCaller.PlatformAdmin));
        return request;
    }

    private static ByteArrayContent Sized(byte[] data)
    {
        var content = new ByteArrayContent(data);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }

    /// <summary>A body whose length is unknown to the client, so it is sent chunked and carries no Content-Length.</summary>
    private static ChunkedContent Chunked(byte[] data) => new(data);

    // ---- success ----

    [Fact]
    public async Task A_valid_body_reaches_the_command_with_the_reason_as_is_and_the_expiry_parsed_as_an_Instant()
    {
        await using var host = await AdminTestHost.StartAsync();

        var response = await AdminEndpointTests.SendAsync(host, "PUT", Path, "{\"reason\":\"  Design-partner pilot  \",\"expiresAt\":\"2030-03-31T23:59:59Z\"}");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var call = host.Commands.Calls.Should().ContainSingle().Which;
        call.Reason.Should().Be("  Design-partner pilot  ", "trimming and length rules stay in the handler");
        call.ExpiresAt.Should().Be(Instant.FromUtc(2030, 3, 31, 23, 59, 59));
        call.Tenant.ToString().Should().Contain(Tenant);
    }

    [Theory]
    [InlineData("{\"reason\":\"Pilot\"}")]
    [InlineData("{\"reason\":\"Pilot\",\"expiresAt\":null}")]
    public async Task An_override_without_an_expiry_lasts_until_revoked_so_the_command_gets_no_expiry(string body)
    {
        await using var host = await AdminTestHost.StartAsync();

        (await AdminEndpointTests.SendAsync(host, "PUT", Path, body)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        host.Commands.Calls.Should().ContainSingle().Which.ExpiresAt.Should().BeNull();
    }

    [Theory]
    [InlineData("application/json; charset=utf-8")]
    [InlineData("application/json; charset=UTF-8")]
    public async Task A_utf_8_charset_on_the_JSON_content_type_is_accepted_whatever_its_case(string contentType)
    {
        await using var host = await AdminTestHost.StartAsync();

        var content = new ByteArrayContent("{\"reason\":\"Pilot\"}"u8.ToArray());
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);

        var response = await SendAsync(host, Put(content));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        host.Commands.Calls.Should().ContainSingle();
    }

    // G6-25-01: a charset other than UTF-8 is a 400, never an exception (an unknown charset makes the framework
    // throw while it picks a decoder), and never logged: no record carries an exception at any level.
    [Theory]
    [InlineData("application/json; charset=bogus")]
    [InlineData("application/json; charset=iso-8859-1")]
    [InlineData("application/json; charset=\"utf-8\"")]
    public async Task A_charset_other_than_utf_8_on_the_JSON_content_type_gets_400_with_no_exception_logged_and_no_command_call(string contentType)
    {
        await using var host = await AdminTestHost.StartAsync();

        var content = new ByteArrayContent("{\"reason\":\"MARKER-9f3a\"}"u8.ToArray());
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);

        var response = await SendAsync(host, Put(content));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await AdminEndpointTests.MembersOf(response)).Should().NotContain(["code", "detail", "instance"]);
        host.Commands.Calls.Should().BeEmpty();
        host.Logs.Records.Should().NotContain(r => r.ExceptionText != null, "no exception is logged at any level, in any category");
        host.Logs.Records.Should().NotContain(r => r.Contains("MARKER-9f3a"));
    }

    // ---- strict JSON ----

    [Theory]
    [MemberData(nameof(RejectedBodies))]
    public async Task A_body_that_fails_schema_validation_gets_400_with_a_generic_ProblemDetails_before_any_command_runs(
        string name, string body, string mediaType)
    {
        await using var host = await AdminTestHost.StartAsync();

        // "no body" is a request with no content at all, not an empty JSON document.
        using var request = Put(new StringContent(body, Encoding.UTF8, mediaType));
        if (name == "no body")
        {
            request.Content = null;
        }

        var response = await SendAsync(host, request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, name);
        var members = await AdminEndpointTests.MembersOf(response);
        members.Should().NotContain(["code", "detail", "instance"], $"{name}: a body error carries no code and never the parser message");
        members.Should().Contain("status");
        host.Commands.Calls.Should().BeEmpty(name);
    }

    [Fact]
    public async Task A_missing_Content_Type_gets_400()
    {
        await using var host = await AdminTestHost.StartAsync();
        var content = new ByteArrayContent("{\"reason\":\"Pilot\"}"u8.ToArray());
        content.Headers.ContentType = null;

        var response = await SendAsync(host, Put(content));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        host.Commands.Calls.Should().BeEmpty();
    }

    // ---- the 8 KiB limit ----

    [Theory]
    [MemberData(nameof(BodyLimitModes))]
    public async Task A_body_of_exactly_8_KiB_with_a_Content_Length_is_read_and_one_byte_more_is_refused(string modeName)
    {
        var mode = Enum.Parse<BodyLimitMode>(modeName);
        await using var host = await AdminTestHost.StartAsync(mode);

        var ok = await SendAsync(host, Put(Sized(BodyOfSize(Limit))));
        var tooBig = await SendAsync(host, Put(Sized(BodyOfSize(Limit + 1))));

        ok.StatusCode.Should().Be(HttpStatusCode.NoContent, "8192 bytes is within the limit");
        tooBig.StatusCode.Should().Be(HttpStatusCode.BadRequest, "8193 bytes is over it");
        host.Commands.Calls.Should().ContainSingle("only the 8192-byte body reached the command");
    }

    [Fact]
    public async Task A_chunked_body_over_8_KiB_is_refused_when_the_server_enforces_the_limit_the_endpoint_set()
    {
        await using var host = await AdminTestHost.StartAsync(BodyLimitMode.Writable);

        // No Content-Length to check: only the limit the endpoint put on the feature stops the read.
        var response = await SendAsync(host, Put(Chunked(BodyOfSize(Limit * 4))));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        host.Commands.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_chunked_body_within_8_KiB_is_read_when_the_server_enforces_the_limit_the_endpoint_set()
    {
        await using var host = await AdminTestHost.StartAsync(BodyLimitMode.Writable);

        var response = await SendAsync(host, Put(Chunked(BodyOfSize(1024))));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        host.Commands.Calls.Should().ContainSingle();
    }

    [Theory]
    [InlineData(nameof(BodyLimitMode.ReadOnly))]
    [InlineData(nameof(BodyLimitMode.Absent))]
    public async Task When_the_limit_cannot_be_set_a_body_without_a_Content_Length_is_refused_even_if_small(string modeName)
    {
        var mode = Enum.Parse<BodyLimitMode>(modeName);
        await using var host = await AdminTestHost.StartAsync(mode);

        // The limit would otherwise silently vanish (T-10): chunked, 100 bytes.
        var response = await SendAsync(host, Put(Chunked(BodyOfSize(100))));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        host.Commands.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData(nameof(BodyLimitMode.ReadOnly))]
    [InlineData(nameof(BodyLimitMode.Absent))]
    public async Task When_the_limit_cannot_be_set_a_declared_Content_Length_within_8_KiB_is_accepted_and_one_above_is_refused(string modeName)
    {
        var mode = Enum.Parse<BodyLimitMode>(modeName);
        await using var host = await AdminTestHost.StartAsync(mode);

        var small = await SendAsync(host, Put(Sized(BodyOfSize(100))));
        var big = await SendAsync(host, Put(Sized(BodyOfSize(Limit * 2))));

        small.StatusCode.Should().Be(HttpStatusCode.NoContent);
        big.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        host.Commands.Calls.Should().ContainSingle();
    }

    // ---- no logging of a parse error, no reason anywhere ----

    [Theory]
    [InlineData("{\"reason\":\"MARKER-9f3a")]
    [InlineData("{\"reason\":\"MARKER-9f3a\",\"reason\":\"MARKER-9f3a\"}")]
    [InlineData("{\"reason\":\"MARKER-9f3a\",\"extra\":\"MARKER-9f3a\"}")]
    [InlineData("{\"reason\":\"MARKER-9f3a\",\"expiresAt\":\"MARKER-9f3a\"}")]
    [InlineData("{\"reason\":7,\"x\":\"MARKER-9f3a\"}")]
    public async Task A_parse_error_is_not_logged_as_an_exception_and_only_the_fixed_rejection_line_is_written(string body)
    {
        await using var host = await AdminTestHost.StartAsync();

        var response = await AdminEndpointTests.SendAsync(host, "PUT", Path, body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var records = host.Logs.Records.ToList();
        records.Should().NotContain(r => r.ExceptionText != null, "no exception is logged for a parse error, at any level, in any category");
        records.Should().NotContain(r => r.Contains("MARKER-9f3a"));
        records.Where(r => r.Level >= LogLevel.Warning).Should().ContainSingle().Which.Should().Match<CapturedLogRecord>(
            r => r.Category == "Decisya.Modules.Admin.Endpoints.AdminRequests"
                && r.Message.Contains("grant_override", StringComparison.Ordinal)
                && (r.Message.Contains("body_invalid", StringComparison.Ordinal) || r.Message.Contains("expires_invalid", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_body_over_the_limit_is_not_logged_as_an_exception_either()
    {
        await using var host = await AdminTestHost.StartAsync(BodyLimitMode.Writable);

        var response = await SendAsync(host, Put(Chunked(BodyOfSize(Limit * 2))));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        host.Logs.Records.Should().NotContain(r => r.ExceptionText != null);
    }

    [Fact]
    public async Task The_reason_is_never_echoed_or_logged()
    {
        var reason = $"MARKER-9f3a-{Guid.NewGuid():N} Contact: anna.meier@example.com, IBAN DE89370400440532013000";
        string[] canaries = ["MARKER-9f3a", "anna.meier@example.com", "DE89370400440532013000"];
        using var telemetry = new TelemetryCapture();
        await using var host = await AdminTestHost.StartAsync();
        var seen = new List<string>();

        async Task SendAndCollectAsync(HttpRequestMessage request)
        {
            var response = await SendAsync(host, request);
            seen.Add(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            seen.AddRange(response.Headers.Select(h => $"{h.Key}: {string.Join(',', h.Value)}"));
            seen.AddRange(response.Content.Headers.Select(h => $"{h.Key}: {string.Join(',', h.Value)}"));
        }

        string Json(string tail) => "{\"reason\":\"" + reason + "\"" + tail + "}";

        // A success, then a request that fails validation in every way a body can, each carrying the reason.
        var bodies = new[]
        {
            Json(string.Empty),
            Json(",\"expiresAt\":\"2030-01-01T00:00:00Z\""),
            Json(",\"expiresAt\":\"" + reason + "\""),
            Json(",\"unknown\":\"" + reason + "\""),
            Json(",\"reason\":\"" + reason + "\""),
            Json(",\"expiresAt\":20300101"),
            "{\"reason\":\"" + reason,
            "{\"reason\":\"" + new string('a', Limit) + reason + "\"}",
        };

        foreach (var body in bodies)
        {
            using var request = Put(new StringContent(body, Encoding.UTF8, "application/json"));
            await SendAndCollectAsync(request);
        }

        // Wrong content type, and a chunked body the endpoint has to refuse.
        using (var wrongType = Put(new StringContent(Json(string.Empty), Encoding.UTF8, "text/plain")))
        {
            await SendAndCollectAsync(wrongType);
        }

        using (var chunked = Put(Chunked(Encoding.UTF8.GetBytes(Json(",\"pad\":\"" + new string('a', Limit * 2) + "\"")))))
        {
            await SendAndCollectAsync(chunked);
        }

        // A malformed path with a valid body, and a facade failure of every status.
        using (var badTenant = Put(new StringContent(Json(string.Empty), Encoding.UTF8, "application/json"), "/api/admin/tenants/not-a-guid/overrides/forecasting.scenarios"))
        {
            await SendAndCollectAsync(badTenant);
        }

        foreach (var (status, code) in new[]
        {
            (Decisya.Modules.Entitlements.Contracts.Admin.EntitlementAdminStatus.Invalid, "entitlements.reason_invalid"),
            (Decisya.Modules.Entitlements.Contracts.Admin.EntitlementAdminStatus.NotFound, "entitlements.tenant_not_found"),
            (Decisya.Modules.Entitlements.Contracts.Admin.EntitlementAdminStatus.Forbidden, "entitlements.forbidden"),
        })
        {
            host.Commands.Result = Decisya.Modules.Entitlements.Contracts.Admin.EntitlementAdminResult.Failed(status, code);
            using var request = Put(new StringContent(Json(string.Empty), Encoding.UTF8, "application/json"));
            await SendAndCollectAsync(request);
        }

        // A forced 500: the facade throws (its message, like the real one's, never carries the request).
        host.Commands.Fault = new InvalidOperationException("The entitlement admin facade returned an undefined status.");
        using (var forced = Put(new StringContent(Json(string.Empty), Encoding.UTF8, "application/json")))
        {
            await SendAndCollectAsync(forced);
        }

        foreach (var canary in canaries)
        {
            seen.Should().NotContain(s => s.Contains(canary, StringComparison.Ordinal), $"no response body or header may contain {canary}");
            host.Logs.Records.Should().NotContain(r => r.Contains(canary), $"no log record at Trace, in any category, may contain {canary}");
            telemetry.Values.Should().NotContain(v => v.Contains(canary, StringComparison.Ordinal), $"no activity or metric tag may contain {canary}");
        }

        host.Logs.Records.Should().NotBeEmpty("the capture works: the host logged at Trace");
        telemetry.AdminTags.Should().NotBeEmpty("the capture works: the admin counter was recorded");
    }

    [Fact]
    public async Task Request_telemetry_names_the_action_and_outcome_and_the_target_tenant_but_no_reason()
    {
        using var telemetry = new TelemetryCapture();
        await using var host = await AdminTestHost.StartAsync();

        (await AdminEndpointTests.SendAsync(host, "PUT", Path, "{\"reason\":\"MARKER-9f3a\"}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        telemetry.AdminTags.Should().Contain(["decisya.admin.action=grant_override", "decisya.admin.outcome=succeeded"]);
        var completed = host.Logs.Records.Should().ContainSingle(r => r.Message.Contains("completed", StringComparison.Ordinal)).Which;
        completed.Level.Should().Be(LogLevel.Information);
        completed.Message.Should().Contain("grant_override").And.Contain("succeeded").And.Contain(Tenant).And.NotContain("MARKER");
    }

    /// <summary>An <see cref="HttpContent"/> whose length is unknown to the client, so it goes out chunked, with no Content-Length.</summary>
    private sealed class ChunkedContent : HttpContent
    {
        private readonly byte[] _data;

        public ChunkedContent(byte[] data)
        {
            _data = data;
            Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
            stream.WriteAsync(_data, 0, _data.Length);

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }
    }
}
