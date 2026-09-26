using System.Diagnostics;
using System.Text.Json;
using Decisya.ServiceDefaults.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;

namespace Decisya.ServiceDefaults.Tests.Logging;

/// <summary>
/// Story 3 (trace correlation), Story 4 (masking, reserved tenant/user fields), NFR-10 and
/// G4-15-20/21 (the formatter never throws, and never emits log-injectable output).
/// </summary>
public class DecisyaJsonConsoleFormatterTests : IDisposable
{
    private static readonly ActivitySource TestSource = new("Decisya.ServiceDefaults.Tests");
    private readonly ActivityListener _listener = new()
    {
        ShouldListenTo = _ => true,
        Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
    };

    public DecisyaJsonConsoleFormatterTests() => ActivitySource.AddActivityListener(_listener);

    public void Dispose()
    {
        _listener.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Trace_and_span_ids_match_the_active_activity()
    {
        var formatter = CreateFormatter(out _, out _);
        using var activity = TestSource.StartActivity("test-activity");
        activity.Should().NotBeNull();

        var json = WriteLine(formatter, LogLevel.Information, "hello");

        json.RootElement.GetProperty("trace_id").GetString().Should().Be(activity!.TraceId.ToHexString());
        json.RootElement.GetProperty("span_id").GetString().Should().Be(activity.SpanId.ToHexString());
    }

    [Fact]
    public void Trace_and_span_ids_are_null_outside_any_activity()
    {
        var formatter = CreateFormatter(out _, out _);

        var json = WriteLine(formatter, LogLevel.Information, "hello");

        json.RootElement.GetProperty("trace_id").ValueKind.Should().Be(JsonValueKind.Null);
        json.RootElement.GetProperty("span_id").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public void Tenant_and_user_are_present_but_null_with_no_enrichment()
    {
        var formatter = CreateFormatter(out _, out _);

        var json = WriteLine(formatter, LogLevel.Information, "hello");

        json.RootElement.TryGetProperty("tenant_id", out var tenant).Should().BeTrue();
        tenant.ValueKind.Should().Be(JsonValueKind.Null);
        json.RootElement.TryGetProperty("user_id", out var user).Should().BeTrue();
        user.ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public void Tenant_and_user_are_populated_from_the_enrichment_context()
    {
        var formatter = CreateFormatter(out _, out var enrichment);
        using (enrichment.Begin("tenant-42", "user-7"))
        {
            var json = WriteLine(formatter, LogLevel.Information, "hello");

            json.RootElement.GetProperty("tenant_id").GetString().Should().Be("tenant-42");
            json.RootElement.GetProperty("user_id").GetString().Should().NotBeNullOrEmpty();
        }
    }

    [Fact]
    public void A_sensitive_value_is_masked_and_never_appears_raw()
    {
        var formatter = CreateFormatter(out _, out _);
        var canary = Canaries.Unique("email");
        var state = new[] { new KeyValuePair<string, object?>("Email", new SensitiveToken(canary)) };

        var line = WriteRawLine(formatter, LogLevel.Information, state, "logging {Email}");

        line.Should().NotContain(canary);
        line.Should().Contain(SensitiveDataMaskingProcessor.Mask);
    }

    [Fact]
    public void A_DecisyaObservabilityOptions_instance_is_masked_end_to_end_on_stdout()
    {
        // G4-15-26: the options type itself, not a hand-picked scalar, logged through the
        // real formatter Write path.
        var formatter = CreateFormatter(out _, out _);
        var key = Canaries.HashKey();
        var options = new DecisyaObservabilityOptions { UserIdHashKey = key };
        var state = new[] { new KeyValuePair<string, object?>("Options", options) };

        var line = WriteRawLine(formatter, LogLevel.Information, state, "options {Options}");

        line.Should().NotContain(key);
        line.Should().Contain(SensitiveDataMaskingProcessor.Mask);
    }

    // --- M-1 (G4-15-16, 17; G6 review): string/Uri members, collection elements, and
    // framework types made only of strings — stdout side ---

    [Fact]
    public void M1_case_a_a_Decisya_record_with_string_and_uri_members_is_masked_on_stdout()
    {
        var formatter = CreateFormatter(out _, out _);
        var jwt = Canaries.JwtShaped();
        var callback = new Uri("https://u:p@h.example/cb?token=x#f");
        var state = new[] { new KeyValuePair<string, object?>("R", new NoteAndCallback(jwt, callback)) };

        var line = WriteRawLine(formatter, LogLevel.Information, state, "logging {R}");

        line.Should().NotContain(jwt);
        line.Should().NotContain("u:p@");
        line.Should().NotContain("token=x");
        line.Should().Contain(SensitiveDataMaskingProcessor.Mask);
    }

    [Fact]
    public void M1_case_b_a_list_of_strings_with_a_jwt_is_masked_on_stdout_including_the_message()
    {
        var formatter = CreateFormatter(out _, out _);
        var jwt = Canaries.JwtShaped();
        var state = new[] { new KeyValuePair<string, object?>("Values", new List<string> { "clean", jwt }) };

        var line = WriteRawLine(formatter, LogLevel.Information, state, "logging {Values}");

        line.Should().NotContain(jwt);
        using var document = JsonDocument.Parse(line);
        // AnyMasked must be true here, so "message" is the raw template, never the
        // MEL-formatted text that would otherwise join the list's raw elements.
        document.RootElement.GetProperty("message").GetString().Should().Be("logging {Values}");
    }

    [Fact]
    public void M1_case_c_an_AuthenticationHeaderValue_is_masked_whole_on_stdout()
    {
        var formatter = CreateFormatter(out _, out _);
        var canary = Canaries.Unique("bearer-token");
        var header = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", canary);
        var state = new[] { new KeyValuePair<string, object?>("Auth", header) };

        var line = WriteRawLine(formatter, LogLevel.Information, state, "logging {Auth}");

        line.Should().NotContain(canary);
        line.Should().Contain(SensitiveDataMaskingProcessor.Mask);
    }

    [Fact]
    public void The_line_is_exactly_one_well_formed_json_object_even_with_injection_attempts()
    {
        var formatter = CreateFormatter(out _, out _);

        // Assembled from numeric char codes rather than embedded as literal escapes in this
        // file, so no editor, formatter or transport step along the way can silently turn an
        // escape sequence into the real control character it names. Covers: CR, LF, U+2028
        // (LINE SEPARATOR), U+2029 (PARAGRAPH SEPARATOR), U+0085 (NEL), ESC (start of an ANSI
        // colour code) and a JSON-breaking substring — anything a naive formatter could let a
        // message, key or value use to forge a second record or a second top-level field.
        var cr = ((char)0x0D).ToString();
        var lf = ((char)0x0A).ToString();
        var lineSeparator = char.ConvertFromUtf32(0x2028);
        var paragraphSeparator = char.ConvertFromUtf32(0x2029);
        var nextLine = ((char)0x85).ToString();
        var escape = ((char)0x1B).ToString();
        var injection = string.Concat(
            cr, lf, " ", lineSeparator, paragraphSeparator, nextLine, escape,
            "[31m\"},\"level\":\"Critical\",\"x\":{\"");
        var state = new[] { new KeyValuePair<string, object?>("Payload", injection) };

        var line = WriteRawLine(formatter, LogLevel.Warning, state, injection, exception: null);

        line.Should().EndWith("\n");
        line.IndexOf('\n', StringComparison.Ordinal).Should().Be(line.Length - 1);
        var document = JsonDocument.Parse(line);
        document.RootElement.GetProperty("level").GetString().Should().Be(nameof(LogLevel.Warning));
        line.Should().NotContain(escape);
        line.Should().NotContain(lineSeparator);
        line.Should().NotContain(paragraphSeparator);
        line.Should().NotContain(nextLine);
    }

    [Fact]
    public void Non_finite_double_values_never_throw_and_still_produce_one_line()
    {
        var formatter = CreateFormatter(out _, out _);
        var state = new[]
        {
            new KeyValuePair<string, object?>("Nan", double.NaN),
            new KeyValuePair<string, object?>("Inf", double.PositiveInfinity),
            new KeyValuePair<string, object?>("FloatNan", float.NaN),
        };

        var line = WriteRawLine(formatter, LogLevel.Information, state, "non-finite values");

        var act = () => JsonDocument.Parse(line);
        act.Should().NotThrow();
    }

    [Fact]
    public void A_lone_surrogate_never_throws_and_still_produces_one_parseable_line()
    {
        var formatter = CreateFormatter(out _, out _);
        var loneSurrogate = ((char)0xD800).ToString();
        var message = string.Concat("before ", loneSurrogate, " after");
        var state = new[] { new KeyValuePair<string, object?>("Key", string.Concat("value ", loneSurrogate, " here")) };

        var line = WriteRawLine(formatter, LogLevel.Information, state, message);

        var act = () => JsonDocument.Parse(line);
        act.Should().NotThrow();
        line.Should().EndWith("\n");
    }

    private static DecisyaJsonConsoleFormatter CreateFormatter(
        out IClock clock,
        out ILogEnrichmentContext enrichment)
    {
        clock = new FakeClock(Instant.FromUtc(2026, 1, 1, 0, 0));
        var hasher = new UserIdHasher(Convert.FromBase64String(Canaries.HashKey()));
        enrichment = new LogEnrichmentContext(hasher);
        return new DecisyaJsonConsoleFormatter(clock, new SensitiveDataMaskingProcessor(), enrichment);
    }

    private static JsonDocument WriteLine(DecisyaJsonConsoleFormatter formatter, LogLevel level, string message) =>
        JsonDocument.Parse(WriteRawLine(formatter, level, state: null, message));

    private static string WriteRawLine(
        DecisyaJsonConsoleFormatter formatter,
        LogLevel level,
        IReadOnlyList<KeyValuePair<string, object?>>? state,
        string message,
        Exception? exception = null)
    {
        var formatterFunc = new Func<FormattedLogState, Exception?, string>(static (s, _) => s.Message);
        var entry = new LogEntry<FormattedLogState>(
            level,
            "Decisya.Test.Category",
            new EventId(1, "TestEvent"),
            new FormattedLogState(message, state),
            exception,
            formatterFunc);

        using var writer = new StringWriter();
        formatter.Write(in entry, scopeProvider: null, writer);
        return writer.ToString();
    }

    /// <summary>Adapts an arbitrary state list plus a formatted message into a single
    /// <c>IReadOnlyList&lt;KeyValuePair&lt;string, object?&gt;&gt;</c>-shaped state, the
    /// shape <see cref="SensitiveDataMaskingProcessor"/> expects.</summary>
    private sealed class FormattedLogState(string message, IReadOnlyList<KeyValuePair<string, object?>>? pairs)
        : IReadOnlyList<KeyValuePair<string, object?>>
    {
        private readonly IReadOnlyList<KeyValuePair<string, object?>> _pairs = Combine(message, pairs);

        internal string Message { get; } = message;

        public int Count => _pairs.Count;

        public KeyValuePair<string, object?> this[int index] => _pairs[index];

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => _pairs.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        private static List<KeyValuePair<string, object?>> Combine(
            string message, IReadOnlyList<KeyValuePair<string, object?>>? pairs)
        {
            var list = new List<KeyValuePair<string, object?>>(pairs ?? []);
            list.Add(new KeyValuePair<string, object?>("{OriginalFormat}", message));
            return list;
        }
    }
}
