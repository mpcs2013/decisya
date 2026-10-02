using Decisya.ArchitectureTests.Fixtures.TenancyCompliant.AuditedCommands;
using Decisya.ArchitectureTests.Fixtures.TenancyCompliant.RawAdoNet;
using Decisya.ArchitectureTests.Fixtures.TenancyCompliant.Transactions;
using Decisya.ArchitectureTests.Fixtures.TenancyViolations.RawAdoNet;
using Decisya.ArchitectureTests.Fixtures.TenancyViolations.Transactions;
using Decisya.ArchitectureTests.Fixtures.TenancyViolations.UnauditedCommands;

namespace Decisya.ArchitectureTests;

/// <summary>
/// Issue #24 (G2, G3): proves <see cref="CrossTenantAuditRule"/> (C-1 and S-1),
/// <see cref="ExplicitTransactionRule"/> (ADR-0013) and <see cref="RawAdoNetRule"/> (G4-24-03)
/// detect real violations ("red") and pass real, correct code ("green"), using the
/// <c>Fixtures/TenancyViolations</c> and <c>Fixtures/TenancyCompliant</c> projects. The real
/// <see cref="ArchitectureScope"/> runs live in <see cref="ModuleBoundaryTests"/>.
/// </summary>
public class AuditRuleTests
{
    private const string UnauditedNamespace = "Decisya.ArchitectureTests.Fixtures.TenancyViolations.UnauditedCommands";
    private const string AuditedNamespace = "Decisya.ArchitectureTests.Fixtures.TenancyCompliant.AuditedCommands";

    // C-1: every [AllowCrossTenant] type appends an audit record.

    [Fact]
    public void CrossTenantAuditRule_fails_on_an_AllowCrossTenant_type_that_never_appends()
    {
        var result = CrossTenantAuditRule.EvaluateIn(UnauditedNamespace, typeof(UnauditedCrossTenantHandler).Assembly);

        result.IsSuccessful.Should().BeFalse();
        result.FailingTypeNames.Should().Contain(t => t.Contains(nameof(UnauditedCrossTenantHandler), StringComparison.Ordinal));
    }

    [Fact]
    public void CrossTenantAuditRule_passes_on_an_AllowCrossTenant_type_that_appends_inside_an_async_method()
    {
        var result = CrossTenantAuditRule.EvaluateIn(AuditedNamespace, typeof(AuditedCrossTenantHandler).Assembly);

        result.IsSuccessful.Should().BeTrue(string.Join(", ", result.FailingTypeNames ?? []));
    }

    // G3 S-1: living in an Audit-named namespace does not exempt a type; neither does reusing the writer's full name in another assembly.

    [Fact]
    public void CrossTenantAuditRule_fails_on_an_attributed_type_in_an_Audit_namespace_that_does_not_append()
    {
        var result = CrossTenantAuditRule.EvaluateIn("Decisya.Modules.Audit.Fixtures", typeof(UnauditedCrossTenantHandler).Assembly);

        result.IsSuccessful.Should().BeFalse();
        result.FailingTypeNames.Should().Contain(t => t.Contains("UnappendingAuditReader", StringComparison.Ordinal));
    }

    [Fact]
    public void CrossTenantAuditRule_does_not_exempt_a_type_that_only_borrows_the_AuditWriter_full_name()
    {
        var result = CrossTenantAuditRule.EvaluateIn("Decisya.Modules.Audit.Application", typeof(UnauditedCrossTenantHandler).Assembly);

        result.IsSuccessful.Should().BeFalse();
        result.FailingTypeNames.Should().Contain("Decisya.Modules.Audit.Application.AuditWriter");
    }

    // ADR-0013: only the four listed types begin or enlist a transaction.

    [Theory]
    [InlineData(nameof(UnlistedTransactionUser))]
    [InlineData(nameof(SavepointsDisabled))]
    [InlineData(nameof(AutoTransactionsDisabled))]
    public void ExplicitTransactionRule_fails_on_an_unlisted_type(string fixtureName)
    {
        var result = ExplicitTransactionRule.Evaluate(new Dictionary<string, string[]>(), typeof(UnlistedTransactionUser).Assembly);

        result.IsSuccessful.Should().BeFalse();
        result.FailingTypeNames.Should().Contain(t => t.Contains(fixtureName, StringComparison.Ordinal));
    }

    [Fact]
    public void ExplicitTransactionRule_passes_a_listed_type()
    {
        var result = ExplicitTransactionRule.Evaluate(
            new Dictionary<string, string[]>
            {
                [typeof(ListedTransactionUser).FullName!] = [.. HandlerMembers, .. WriterMembers],
                [typeof(EnlistOnly).FullName!] = WriterMembers,
            },
            typeof(ListedTransactionUser).Assembly);

        result.IsSuccessful.Should().BeTrue(string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Theory]
    [InlineData(nameof(SavepointsDisabled))]
    [InlineData(nameof(AutoTransactionsDisabled))]
    public void ExplicitTransactionRule_never_lets_a_listed_type_turn_off_savepoints_or_automatic_transactions(string fixtureName)
    {
        var allowed = new Dictionary<string, string[]>
        {
            [typeof(SavepointsDisabled).FullName!] = HandlerMembers,
            [typeof(AutoTransactionsDisabled).FullName!] = HandlerMembers,
            [typeof(UnlistedTransactionUser).FullName!] = [.. HandlerMembers, .. WriterMembers],
        };

        var result = ExplicitTransactionRule.Evaluate(allowed, typeof(SavepointsDisabled).Assembly);

        result.IsSuccessful.Should().BeFalse();
        result.FailingTypeNames.Should().Contain(t => t.Contains(fixtureName, StringComparison.Ordinal));
    }

    // G4-24-03: no raw ADO.NET and no DbTransaction lifecycle call anywhere in scope.

    [Theory]
    [InlineData(nameof(TransactionConnectionCreateCommand))]
    [InlineData(nameof(DbTransactionCommit))]
    [InlineData(nameof(DbTransactionRollback))]
    [InlineData(nameof(NewNpgsqlCommand))]
    [InlineData(nameof(NewNpgsqlConnection))]
    [InlineData(nameof(DbCommandExecute))]
    [InlineData(nameof(DbConnectionClose))]
    [InlineData(nameof(CopyExportThroughCast))]
    [InlineData(nameof(DataAdapterFill))]
    public void RawAdoNetRule_fails_on_raw_ADO_NET(string fixtureName)
    {
        var result = RawAdoNetRule.Evaluate(typeof(TransactionConnectionCreateCommand).Assembly);

        result.IsSuccessful.Should().BeFalse();
        result.FailingTypeNames.Should().Contain(t => t.Contains(fixtureName, StringComparison.Ordinal));
    }

    [Fact]
    public void RawAdoNetRule_passes_a_type_that_only_reads_the_connection_and_enlists_through_EF()
    {
        var result = RawAdoNetRule.Evaluate(typeof(EnlistOnly).Assembly);

        result.IsSuccessful.Should().BeTrue(string.Join(", ", result.FailingTypeNames ?? []));
    }

    // G6-24-02: per-type allowed members.

    private static readonly string[] HandlerMembers = ["BeginTransaction", "BeginTransactionAsync", "Commit", "CommitAsync", "Rollback", "RollbackAsync"];
    private static readonly string[] WriterMembers = ["UseTransaction", "UseTransactionAsync"];

    [Theory]
    [InlineData(nameof(WriterLikeCommitter))]
    [InlineData(nameof(WriterLikeRoller))]
    public void ExplicitTransactionRule_fails_a_writer_listed_type_that_commits_or_rolls_back_through_EF(string fixtureName)
    {
        var allowed = new Dictionary<string, string[]>
        {
            [typeof(WriterLikeCommitter).FullName!] = WriterMembers,
            [typeof(WriterLikeRoller).FullName!] = WriterMembers,
        };

        var result = ExplicitTransactionRule.Evaluate(allowed, typeof(WriterLikeCommitter).Assembly);

        result.IsSuccessful.Should().BeFalse();
        result.FailingTypeNames.Should().Contain(t => t.Contains(fixtureName, StringComparison.Ordinal));
    }

    [Fact]
    public void ExplicitTransactionRule_fails_a_handler_listed_type_that_creates_a_savepoint_by_hand()
    {
        var allowed = new Dictionary<string, string[]> { [typeof(HandlerLikeSavepointUser).FullName!] = HandlerMembers };

        var result = ExplicitTransactionRule.Evaluate(allowed, typeof(HandlerLikeSavepointUser).Assembly);

        result.IsSuccessful.Should().BeFalse();
        result.FailingTypeNames.Should().Contain(t => t.Contains(nameof(HandlerLikeSavepointUser), StringComparison.Ordinal));
    }

    [Fact]
    public void ExplicitTransactionRule_passes_a_handler_listed_type_that_begins_and_commits()
    {
        var allowed = new Dictionary<string, string[]>
        {
            [typeof(ListedTransactionUser).FullName!] = [.. HandlerMembers, .. WriterMembers],
            [typeof(EnlistOnly).FullName!] = WriterMembers,
        };

        var result = ExplicitTransactionRule.Evaluate(allowed, typeof(ListedTransactionUser).Assembly);

        result.IsSuccessful.Should().BeTrue(string.Join(", ", result.FailingTypeNames ?? []));
    }
}
