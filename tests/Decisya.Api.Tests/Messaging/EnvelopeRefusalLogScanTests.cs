using System.Reflection;
using System.Reflection.Emit;
using Decisya.Api.Authentication;
using Decisya.Api.Messaging;
using Decisya.ServiceDefaults.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wolverine;

namespace Decisya.Api.Tests.Messaging;

/// <summary>
/// Issue #231, G1 Story 1 ("that event holds no payload, no raw tenant value and no message body"), G3 T-13 and
/// T-14, and the G2 note "extend LogScanTests": the log scan for the new refusal event. It follows
/// <c>Decisya.Bff.Tests/LogScanTests</c>: one in-memory provider captures every record the host writes, with
/// the minimum level forced to Debug, across refused messages (every kind of bad tenant, and a missing caller)
/// and a valid one, and no record's message, structured state or exception text may carry the payload, a raw
/// tenant value or the caller id. The Bff scan cannot cover this event: the Bff has no Wolverine and writes
/// none of these records. Wolverine's own categories must still sit at the Warning floor with every other
/// category at Debug, because their Information and Debug logs describe envelopes (G3 S-05).
/// </summary>
[Trait("Category", "Unit")]
[Collection(ChainHostDefinition.Name)]
public class EnvelopeRefusalLogScanTests
{
    private const string PayloadCanary = "payload-canary-6a1f";
    private const string RawTenantCanary = "raw-tenant-canary-4417";
    private const string CallerCanary = "caller-canary-8821";
    private const string ValidTenant = "018f2c9e-6b7a-7c3e-8b1a-2f3c4d5e6f70";

    [Fact]
    public async Task No_log_record_across_refused_and_valid_messages_carries_the_payload_a_raw_tenant_value_or_the_caller_id()
    {
        await using var host = await MessagingHost.StartAsync(
            "Production", configure: builder => builder.Logging.SetMinimumLevel(LogLevel.Debug));

        (string? Tenant, string? Caller)[] refusals =
        [
            (null, CallerCanary),
            (string.Empty, CallerCanary),
            (RawTenantCanary, CallerCanary),
            (" " + ValidTenant + " ", CallerCanary),
            ("00000000-0000-0000-0000-000000000000", CallerCanary),
            ("*DEFAULT*", CallerCanary),
            (ValidTenant, null),
            (ValidTenant, " "),
        ];

        foreach (var (tenant, caller) in refusals)
        {
            await using var scope = host.Services.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<IMessageContext>();
            var options = new DeliveryOptions { TenantId = tenant };
            if (caller is not null)
            {
                options.Headers[EnvelopeFacts.CallerIdHeader] = caller;
            }

            await context.InvokeAsync(new ProbeMessage(PayloadCanary), options, TestContext.Current.CancellationToken);
        }

        var afterRefusals = host.Logs.Records.ToList();
        var events = afterRefusals.Where(r => r.EventName == "EnvelopeRefused").ToList();
        events.Should().HaveCount(refusals.Length, "each refused message writes exactly one event");
        events.Should().OnlyContain(e => e.StateText.Contains("Reason=", StringComparison.Ordinal));
        host.Recorder.Observed.Any(o => o.Label == PayloadCanary).Should().BeFalse("a refused message runs no handler");

        // The refusal phase: no payload, no raw tenant text (the bad value, or a valid tenant inside padding), no caller id.
        AssertNone(afterRefusals, PayloadCanary, RawTenantCanary, ValidTenant, CallerCanary);

        // A valid message next: the handler runs, and still no payload or caller id reaches a log record.
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<IMessageContext>();
            var options = new DeliveryOptions { TenantId = ValidTenant };
            options.Headers[EnvelopeFacts.CallerIdHeader] = CallerCanary;

            await context.InvokeAsync(new ProbeMessage(PayloadCanary + "-valid"), options, TestContext.Current.CancellationToken);
        }

        host.Recorder.Observed.Any(o => o.Label == PayloadCanary + "-valid").Should().BeTrue("the valid message is handled");

        var all = host.Logs.Records.ToList();
        AssertNone(all, PayloadCanary, CallerCanary);
        all.Count(r => r.EventName == "EnvelopeRefused").Should().Be(refusals.Length, "the valid message adds no refusal event");

        // Every other category is at Debug in this host, but Wolverine's and JasperFx's stay at the Warning floor.
        all.Where(r => r.Category.StartsWith("Wolverine", StringComparison.Ordinal) || r.Category.StartsWith("JasperFx", StringComparison.Ordinal))
            .Should().OnlyContain(r => r.Level >= LogLevel.Warning);
    }

    // G3 S-04 and T-13: the event names the resolved message's CLR type, capped at 256 characters.
    [Fact]
    public async Task The_refusal_event_names_the_message_CLR_type_capped_at_256_characters()
    {
        await using var host = await MessagingHost.StartAsync(probeHandlers: false);
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("LongMessageTypeFixture"), AssemblyBuilderAccess.Run);
        var type = assembly.DefineDynamicModule("LongMessageTypeFixture")
            .DefineType("LongMessageTypeFixture." + new string('T', 300), TypeAttributes.Public | TypeAttributes.Class)
            .CreateType();
        var envelope = new Envelope(Activator.CreateInstance(type)!) { TenantId = null };

        await using var scope = host.Services.CreateAsyncScope();
        var middleware = new EnvelopeTenantMiddleware(
            scope.ServiceProvider.GetRequiredService<RequestCaller>(),
            scope.ServiceProvider.GetRequiredService<ILogEnrichmentContext>(),
            scope.ServiceProvider.GetRequiredService<ILogger<EnvelopeTenantMiddleware>>());
        var logsBefore = host.Logs.Records.Count;

        middleware.Before(envelope).Should().Be(HandlerContinuation.Stop);

        var refused = host.Logs.Records.Skip(logsBefore).Single(r => r.EventName == "EnvelopeRefused");
        var fullName = type.FullName!;
        fullName.Length.Should().BeGreaterThan(256);
        refused.StateText.Should().Contain("MessageType=" + fullName[..256]);
        refused.Contains(fullName[..257]).Should().BeFalse("the type name is cut at 256 characters");
    }

    private static void AssertNone(IEnumerable<CapturedLogRecord> records, params string[] values)
    {
        foreach (var record in records)
        {
            foreach (var value in values)
            {
                record.Contains(value).Should().BeFalse(
                    $"[{record.Category}] event '{record.EventName}' must not carry '{value}' (message: '{record.Message}')");
            }
        }
    }
}
