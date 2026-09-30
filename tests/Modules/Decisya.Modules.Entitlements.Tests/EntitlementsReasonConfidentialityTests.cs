using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Decisya.Modules.Entitlements.Application;
using Decisya.Modules.Entitlements.Contracts;
using Decisya.Modules.Entitlements.Domain;
using Decisya.ServiceDefaults.Logging;
using Decisya.SharedKernel.Tenancy;
using Decisya.TestInfrastructure;
using Microsoft.Extensions.Logging;
using NodaTime;
using static Decisya.Modules.Entitlements.Tests.TestSupport.EntitlementsHarness;

namespace Decisya.Modules.Entitlements.Tests;

/// <summary>
/// G3 G4-23-05 (R-3): the override reason never leaves the database in clear. A unique marker
/// in the reason of every kind of command outcome (created, replaced, rejected, refused) must
/// appear in no captured log record (Decisya, EF Core and Npgsql categories at Trace), no error
/// message, no metric tag and no activity tag.
/// </summary>
public sealed class EntitlementsReasonConfidentialityTests(PostgresFixture pg)
{
    private static readonly FeatureKey Pro = FeatureKeys.ForecastingScenarios;

    [Trait("Category", "Integration")]
    [Fact]
    public async Task A_marker_reason_never_appears_in_captured_logs_error_messages_metric_tags_or_activity_tags()
    {
        var ct = TestContext.Current.CancellationToken;
        var marker = $"MARKER-{Guid.NewGuid():N}";
        await using var h = await CreateAsync(pg, ct);

        var tagValues = new List<string>();
        var sync = new object();
        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == EntitlementsModule.TelemetryName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        meterListener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            lock (sync)
            {
                foreach (var tag in tags)
                {
                    tagValues.Add($"{tag.Key}={tag.Value}");
                }
            }
        });
        meterListener.Start();

        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == EntitlementsModule.TelemetryName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                lock (sync)
                {
                    tagValues.Add(activity.DisplayName);
                    tagValues.AddRange(activity.Tags.Select(t => $"{t.Key}={t.Value}"));
                    tagValues.AddRange(activity.TagObjects.Select(t => $"{t.Key}={t.Value}"));
                    tagValues.AddRange(activity.Events.SelectMany(e => e.Tags.Select(t => $"{t.Key}={t.Value}")));
                }
            },
        };
        ActivitySource.AddActivityListener(activityListener);

        var errors = new List<string>();

        // Created, then replaced.
        (await h.GrantAsync(new GrantOverride(TenantA, Pro, $"first {marker}", null), ct)).IsSuccess.Should().BeTrue();
        h.Clock.Advance(Duration.FromHours(1));
        (await h.GrantAsync(new GrantOverride(TenantA, Pro, $"replaced {marker}", null), ct)).IsSuccess.Should().BeTrue();

        // reason_invalid: 501 characters, marker inside.
        var tooLong = await h.GrantAsync(new GrantOverride(TenantA, Pro, marker + new string('x', FeatureOverride.MaxReasonLength), null), ct);
        tooLong.Error.Code.Should().Be("entitlements.reason_invalid");
        errors.Add(tooLong.Error.Message);
        errors.Add(tooLong.ToString());

        // expiry_not_in_future.
        var pastExpiry = await h.GrantAsync(new GrantOverride(TenantA, Pro, $"past {marker}", h.Clock.GetCurrentInstant() - Duration.FromDays(1)), ct);
        pastExpiry.Error.Code.Should().Be("entitlements.expiry_not_in_future");
        errors.Add(pastExpiry.Error.Message);
        errors.Add(pastExpiry.ToString());

        // feature_unknown.
        var unknown = await h.GrantAsync(new GrantOverride(TenantA, FeatureKey.Create("nosuch.feature"), $"unknown {marker}", null), ct);
        errors.Add(unknown.Error.Message);

        // forbidden, for a tenant caller and for an invalid ambient.
        var forbiddenTenant = await h.GrantAsync(new GrantOverride(TenantA, Pro, $"forbidden {marker}", null), ct, TenantResolution.For(TenantA));
        var forbiddenInvalid = await h.GrantAsync(new GrantOverride(TenantA, Pro, $"forbidden {marker}", null), ct, TenantResolution.Invalid);
        forbiddenTenant.Error.Code.Should().Be("entitlements.forbidden");
        errors.Add(forbiddenTenant.Error.Message);
        errors.Add(forbiddenInvalid.Error.Message);

        // A revoke and a service call for good measure.
        (await h.RevokeAsync(new RevokeOverride(TenantA, Pro), ct)).IsSuccess.Should().BeTrue();
        await h.IsEnabledAsync(TenantA, Pro, ct);

        // Sanity: the capture saw the module and the persistence stack, at Debug or below.
        var records = h.Logs.Records.ToList();
        records.Should().Contain(r => r.Category.StartsWith("Decisya.Modules.Entitlements", StringComparison.Ordinal));
        records.Should().Contain(r => r.Category.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal), "EF Core's command log must be part of the capture");
        records.Should().Contain(r => r.Level == LogLevel.Warning, "the forbidden refusals log a warning");

        records.Where(r => r.Contains(marker)).Should().BeEmpty("no log record may carry the reason");
        errors.Should().NotContain(e => e.Contains(marker, StringComparison.Ordinal), "no error message may carry the reason");
        lock (sync)
        {
            tagValues.Should().NotBeEmpty("the listeners must have seen the module's instruments and activities");
            tagValues.Where(t => t.Contains(marker, StringComparison.Ordinal)).Should().BeEmpty("no metric or activity tag may carry the reason");
        }
    }

    [Trait("Category", "Integration")]
    [Fact]
    public async Task A_trial_or_tenant_log_line_names_only_the_fixed_fields_and_never_the_raw_command()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(pg, ct);

        (await h.StartTrialAsync(new StartTrial(TenantA), ct)).IsSuccess.Should().BeTrue();
        (await h.GrantAsync(new GrantOverride(TenantA, Pro, "pilot", null), ct)).IsSuccess.Should().BeTrue();

        var own = h.Logs.Records.Where(r => r.Category.StartsWith("Decisya.Modules.Entitlements", StringComparison.Ordinal)).ToList();
        own.Should().HaveCount(2);
        own.Should().OnlyContain(r => r.Level == LogLevel.Information);
        own.Should().OnlyContain(r => !r.Contains("GrantOverride {") && !r.Contains("Reason"));
    }

    [Trait("Category", "Unit")]
    [Fact]
    public void The_masking_processor_renders_the_Reason_of_a_logged_GrantOverride_and_a_FeatureOverride_as_masked()
    {
        var marker = $"MARKER-{Guid.NewGuid():N}";
        var processor = new SensitiveDataMaskingProcessor();
        var command = new GrantOverride(TenantA, Pro, marker, null);
        var entity = new FeatureOverride(TenantA, Pro, marker, Start, null);

        foreach (var value in new object[] { command, entity })
        {
            var rendered = processor.ProcessValue("value", value, out var masked);

            masked.Should().BeTrue(value.GetType().Name);
            var text = rendered as string ?? JsonSerializer.Serialize(rendered);
            text.Should().NotContain(marker, value.GetType().Name);
            text.Should().Contain(SensitiveDataMaskingProcessor.Mask, value.GetType().Name);
        }
    }

    [Trait("Category", "Unit")]
    [Fact]
    public void The_reason_is_marked_Sensitive_on_the_entity_property_and_on_the_command_record_property()
    {
        typeof(FeatureOverride).GetProperty(nameof(FeatureOverride.Reason))!
            .GetCustomAttributes(typeof(Decisya.SharedKernel.Observability.SensitiveAttribute), inherit: true).Should().NotBeEmpty();
        typeof(GrantOverride).GetProperty(nameof(GrantOverride.Reason))!
            .GetCustomAttributes(typeof(Decisya.SharedKernel.Observability.SensitiveAttribute), inherit: true).Should().NotBeEmpty();
    }
}
