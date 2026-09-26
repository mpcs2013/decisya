using Decisya.ServiceDefaults.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;

// Ad-hoc ILogger.Log(...) calls (rather than source-generated LoggerMessage delegates) are
// the point of these tests: they exercise the pipeline with arbitrary, per-test state
// values (a canary, a sensitive-typed value, an exception), which a fixed LoggerMessage
// template cannot express.
#pragma warning disable CA1848, CA1873

namespace Decisya.ServiceDefaults.Tests.Logging;

/// <summary>
/// The OTLP side of the pipeline, exercised through a real (minimal) OpenTelemetry
/// LoggerProvider so <see cref="MaskingLogRecordProcessor"/> runs against a genuine
/// <see cref="LogRecord"/> rather than a hand-built one (the SDK does not expose a public
/// constructor for it). G4-15-09 (processor order / OTLP masking) and G4-15-19 (the
/// exception seam on the OTLP side).
/// </summary>
public class MaskingLogRecordProcessorTests
{
    [Fact]
    public void A_sensitive_value_is_masked_before_it_reaches_the_next_processor()
    {
        var canary = Canaries.Unique("otlp-email");
        using var harness = new Harness();

        harness.Logger.Log(LogLevel.Information, "customer {Email}", new SensitiveToken(canary));
        harness.Provider.ForceFlush();

        harness.Sink.Should().ContainSingle();
        var record = harness.Sink[0];
        record.Attributes.Should().NotContain(p => Equals(p.Value, canary));
        record.Attributes.Should().Contain(p => p.Key == "Email" && Equals(p.Value, SensitiveDataMaskingProcessor.Mask));
        record.FormattedMessage.Should().BeNull();
    }

    [Fact]
    public void Tenant_and_user_are_appended_from_the_enrichment_context()
    {
        using var harness = new Harness();

        using (harness.Enrichment.Begin("tenant-1", "user-1"))
        {
            harness.Logger.Log(LogLevel.Information, "hello");
        }

        harness.Provider.ForceFlush();
        var record = harness.Sink.Single();
        record.Attributes.Should().Contain(p => p.Key == "tenant_id" && Equals(p.Value, "tenant-1"));
        record.Attributes.Should().Contain(p => p.Key == "user_id");
    }

    [Fact]
    public void The_exception_is_moved_into_masked_attributes_and_cleared_from_the_record()
    {
        var jwt = Canaries.JwtShaped();
        using var harness = new Harness();

        harness.Logger.Log(LogLevel.Error, new InvalidOperationException($"boom {jwt}"), "failed");
        harness.Provider.ForceFlush();

        var record = harness.Sink.Single();
        record.HadException.Should().BeFalse("the seam clears LogRecord.Exception once it has copied the masked text out");
        record.Attributes.Should().Contain(p => p.Key == "exception.type");
        var message = (string)record.Attributes.Single(p => p.Key == "exception.message").Value!;
        message.Should().NotContain(jwt);
    }

    /// <summary>A snapshot copied out of a real <see cref="LogRecord"/> during
    /// <see cref="CollectingLogProcessor.OnEnd"/>, since the SDK recycles LogRecord
    /// instances after export and offers no public way to construct one directly.</summary>
    internal sealed record CapturedLogRecord(
        IReadOnlyList<KeyValuePair<string, object?>> Attributes, string? FormattedMessage, bool HadException);

    /// <summary>Wires a minimal OpenTelemetry LoggerProvider whose pipeline is the masking
    /// processor followed by a trivial in-memory collecting processor, mirroring the
    /// production order without needing a network OTLP receiver (the G2 design's rationale
    /// for not adding the OpenTelemetry.Exporter.InMemory package).</summary>
    private sealed class Harness : IDisposable
    {
        private readonly ServiceProvider _services;

        internal Harness()
        {
            var masking = new SensitiveDataMaskingProcessor();
            var hasher = new UserIdHasher(Convert.FromBase64String(Canaries.HashKey()));
            Enrichment = new LogEnrichmentContext(hasher);
            Sink = [];

            var collection = new ServiceCollection();
            collection.AddLogging(builder =>
            {
                builder.SetMinimumLevel(LogLevel.Trace);
                builder.AddOpenTelemetry(options =>
                {
                    options.IncludeFormattedMessage = true;
                    options.AddProcessor(new MaskingLogRecordProcessor(masking, Enrichment));
                    options.AddProcessor(new CollectingLogProcessor(Sink));
                });
            });

            _services = collection.BuildServiceProvider();
            Provider = _services.GetRequiredService<LoggerProvider>();
            Logger = _services.GetRequiredService<ILoggerFactory>().CreateLogger("Decisya.Test");
        }

        internal LogEnrichmentContext Enrichment { get; }

        internal List<CapturedLogRecord> Sink { get; }

        internal LoggerProvider Provider { get; }

        internal ILogger Logger { get; }

        public void Dispose() => _services.Dispose();
    }

    private sealed class CollectingLogProcessor(List<CapturedLogRecord> sink) : OpenTelemetry.BaseProcessor<LogRecord>
    {
        public override void OnEnd(LogRecord data)
        {
            var captured = new CapturedLogRecord(
                data.Attributes?.ToList() ?? [],
                data.FormattedMessage,
                data.Exception is not null);

            lock (sink)
            {
                sink.Add(captured);
            }
        }
    }
}
