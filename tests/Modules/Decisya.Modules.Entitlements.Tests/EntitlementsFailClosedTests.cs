using System.Diagnostics.Metrics;
using Decisya.Modules.Entitlements.Contracts;
using Decisya.SharedKernel.Tenancy;
using static Decisya.Modules.Entitlements.Tests.TestSupport.EntitlementsHarness;

namespace Decisya.Modules.Entitlements.Tests;

/// <summary>
/// G3 G4-23-03 and NFR-35: evaluation fails closed. An unknown key, <c>default(FeatureKey)</c>,
/// an ambient of <c>None</c> or <c>Invalid</c> is denied without an exception and without database
/// access (a placeholder host proves it: any query would throw). A database error propagates and
/// is never turned into a grant.
/// </summary>
[Trait("Category", "Unit")]
public sealed class EntitlementsFailClosedTests
{
    private static readonly FeatureKey Free = FeatureKeys.LedgerTransactions;
    private static readonly FeatureKey Pro = FeatureKeys.ForecastingScenarios;

    private static readonly string[] AllowedFeatureTags = ["unknown", "ledger.transactions", "forecasting.scenarios"];

    [Fact]
    public async Task Under_a_tenant_less_ambient_every_key_is_denied_with_no_exception_and_no_database_access()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = CreateUnreachable();

        foreach (var key in new[] { default, FeatureKey.Create("nosuch.feature"), Pro, Free })
        {
            var act = () => h.IsEnabledAsync(TenantResolution.NoTenant, key, ct);

            (await act.Should().NotThrowAsync($"key '{key}'")).Which.Should().BeFalse($"key '{key}'");
        }
    }

    [Fact]
    public async Task Under_an_invalid_ambient_every_key_is_denied_with_no_exception_and_no_database_access()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = CreateUnreachable();

        foreach (var key in new[] { default, FeatureKey.Create("nosuch.feature"), Pro, Free })
        {
            var act = () => h.IsEnabledAsync(TenantResolution.Invalid, key, ct);

            (await act.Should().NotThrowAsync($"key '{key}'")).Which.Should().BeFalse($"key '{key}'");
        }
    }

    [Fact]
    public async Task An_unknown_key_and_default_FeatureKey_are_denied_for_a_tenant_without_touching_the_database()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = CreateUnreachable();

        (await h.IsEnabledAsync(TenantA, default, ct)).Should().BeFalse();
        (await h.IsEnabledAsync(TenantA, FeatureKey.Create("nosuch.feature"), ct)).Should().BeFalse();
    }

    [Fact]
    public async Task A_Free_key_is_granted_to_a_tenant_without_a_database_query()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = CreateUnreachable();

        (await h.IsEnabledAsync(TenantA, Free, ct)).Should().BeTrue();
    }

    [Fact]
    public async Task A_database_error_propagates_and_is_never_turned_into_allowed_or_denied()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = CreateUnreachable();

        var act = () => h.IsEnabledAsync(TenantA, Pro, ct);

        await act.Should().ThrowAsync<Exception>("a caller's policy turns it into the generic 500, never into allowed");
    }

    [Fact]
    public async Task The_evaluation_counter_records_the_catalog_key_or_unknown_and_never_throws_on_a_default_key()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = CreateUnreachable();
        var measurements = new List<(string? Feature, string? Outcome, string? Source)>();
        var sync = new object();

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == EntitlementsModule.TelemetryName && instrument.Name == "decisya.entitlements.evaluations")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            string? feature = null;
            string? outcome = null;
            string? source = null;
            foreach (var tag in tags)
            {
                switch (tag.Key)
                {
                    case "decisya.entitlements.feature":
                        feature = tag.Value?.ToString();
                        break;
                    case "decisya.entitlements.outcome":
                        outcome = tag.Value?.ToString();
                        break;
                    case "decisya.entitlements.source":
                        source = tag.Value?.ToString();
                        break;
                }
            }

            lock (sync)
            {
                measurements.Add((feature, outcome, source));
            }
        });
        listener.Start();

        await h.IsEnabledAsync(TenantResolution.Invalid, default, ct);
        await h.IsEnabledAsync(TenantResolution.NoTenant, FeatureKey.Create("nosuch.feature"), ct);
        await h.IsEnabledAsync(TenantResolution.NoTenant, Pro, ct);
        await h.IsEnabledAsync(TenantA, Free, ct);

        lock (sync)
        {
            // Other tests in this assembly run in parallel and share the static meter, so check
            // what this test is owed, and that no measurement anywhere carries an unbounded tag value.
            measurements.Should().Contain(("unknown", "denied", "none"));
            measurements.Should().Contain(("forecasting.scenarios", "denied", "none"));
            measurements.Should().Contain(("ledger.transactions", "granted", "plan"));
            measurements.Select(m => m.Feature).Should().OnlyContain(f => f != null && AllowedFeatureTags.Contains(f));
        }
    }
}
