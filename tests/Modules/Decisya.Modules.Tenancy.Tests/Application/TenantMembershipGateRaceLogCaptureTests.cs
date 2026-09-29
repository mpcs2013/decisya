using Decisya.Modules.Tenancy.Application;
using Decisya.Modules.Tenancy.Infrastructure;
using Decisya.Modules.Tenancy.Tests.TestSupport;
using Decisya.SharedKernel.Tenancy;
using Decisya.TestInfrastructure;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Decisya.Modules.Tenancy.Tests.Application;

/// <summary>
/// G3 G4-21-04's own red test, applied to the G4-21-03 race (issue #21): "The G4-21-03 race
/// tests run with log capture at Debug for Microsoft.EntityFrameworkCore, Npgsql and Decisya.
/// No record contains a raw sub or the other tenant's id." <see cref="TenantMembershipGateLog"/>
/// never formats a claim value into a message by construction (see its own remarks), and EF
/// Core's default command logging redacts parameter values unless
/// <c>EnableSensitiveDataLogging</c> is on (banned outside Development/tests, G3 T-10) — this
/// test proves both hold under the real race, against a real Postgres database, rather than
/// trusting either by inspection alone.
/// </summary>
[Trait("Category", "Integration")]
public sealed class TenantMembershipGateRaceLogCaptureTests(PostgresFixture pg)
{
    private const int RaceCallCount = 16;

    [Fact]
    public async Task The_two_user_provisioning_race_captures_no_raw_sub_or_tenant_id_at_Debug_level()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var t = TenantId.New();
        const string userA = "dev-alice";
        const string userB = "dev-bob";
        var db = await pg.CreateDatabaseAsync<TenancyDbContext>(cancellationToken);

        var capturingProvider = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddProvider(capturingProvider);
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Debug);
            builder.AddFilter("Npgsql", LogLevel.Debug);
            builder.AddFilter("Decisya", LogLevel.Debug);
        });

        var callsPerUser = RaceCallCount / 2;
        var tasks = Enumerable.Range(0, callsPerUser)
            .SelectMany(_ => new[]
            {
                RunGateAsync(db, t, userA, loggerFactory, cancellationToken),
                RunGateAsync(db, t, userB, loggerFactory, cancellationToken),
            })
            .ToArray();

        await Task.WhenAll(tasks);

        capturingProvider.Records.Should().NotBeEmpty(
            "the race must have produced at least some EF Core/Npgsql/Decisya log records to scan");

        foreach (var record in capturingProvider.Records)
        {
            record.Contains(userA).Should().BeFalse($"category {record.Category} must never log the raw sub {userA}");
            record.Contains(userB).Should().BeFalse($"category {record.Category} must never log the raw sub {userB}");
            record.Contains(t.ToString()).Should().BeFalse($"category {record.Category} must never log the tenant id {t}");
        }
    }

    private static async Task RunGateAsync(
        PostgresTestDatabase<TenancyDbContext> db, TenantId t, string userId, ILoggerFactory loggerFactory, CancellationToken cancellationToken)
    {
        await using var ctx = db.CreateContext(new TestCurrentTenant { Resolution = TenantResolution.For(t) }, loggerFactory);
        var gate = new TenantMembershipGate(
            ctx,
            new TestCurrentTenant { Resolution = TenantResolution.For(t) },
            new StubCurrentCaller(userId),
            SystemClock.Instance,
            loggerFactory.CreateLogger<TenantMembershipGate>());

        await gate.EnsureAsync(cancellationToken);
    }
}
