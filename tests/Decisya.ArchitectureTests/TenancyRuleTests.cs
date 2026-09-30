using Decisya.ArchitectureTests.Fixtures.TenancyCompliant;
using Decisya.ArchitectureTests.Fixtures.TenancyViolations;

namespace Decisya.ArchitectureTests;

/// <summary>
/// Proves each rule actually detects a real violation ("red") and passes real, correct code
/// ("green") — the same #32 <c>ModuleClockUsageRuleTests</c> pattern — using the
/// <c>Fixtures/TenancyViolations</c> and <c>Fixtures/TenancyCompliant</c> projects. This is the
/// G4 proof for issue #22's five MUSTs, to the extent each is provable without a live Postgres
/// (see the G4 report for #22 for the Postgres-dependent remainder).
/// </summary>
public class TenancyRuleTests
{
    // Story 7 / the issue's Done-when: TenantModelRule.

    [Fact]
    public void TenantModelRule_fails_on_a_context_mapping_an_unscoped_entity()
    {
        var result = TenantModelRule.Evaluate(typeof(ViolatingDbContext).Assembly);

        result.IsSuccessful.Should().BeFalse();
        result.FailingTypeNames.Should().Contain(t => t.Contains(nameof(ViolatingDbContext), StringComparison.Ordinal));
    }

    [Fact]
    public void TenantModelRule_passes_on_a_context_mapping_only_scoped_entities()
    {
        var result = TenantModelRule.Evaluate(typeof(CompliantDbContext).Assembly);

        result.IsSuccessful.Should().BeTrue();
    }

    // G6-22-02: TenantModelRule must catch a TenantId whose concurrency token was stripped by
    // a finalizing convention added after the sealed OnModelCreating ran (ConfigureConventions
    // is overridable, not sealed).

    [Fact]
    public void TenantModelRule_fails_on_a_context_whose_finalizing_convention_strips_the_concurrency_token()
    {
        var result = TenantModelRule.Evaluate(typeof(StrippedConcurrencyTokenDbContext).Assembly);

        result.IsSuccessful.Should().BeFalse();
        result.FailingTypeNames.Should().Contain(t => t.Contains(nameof(StrippedConcurrencyTokenDbContext), StringComparison.Ordinal));
    }

    // G6-22-01: an owned type stored outside its owner's table (OwnsMany; or
    // OwnsOne(...).ToTable(...)) carries no tenant_id of its own. TenantModelRule must fail the
    // model build for it; a table-split OwnsOne (the default) must still pass.

    [Fact]
    public void TenantModelRule_fails_on_a_context_with_an_OwnsMany_owned_type_in_its_own_table()
    {
        var result = TenantModelRule.Evaluate(typeof(OwnedCollectionDbContext).Assembly);

        result.IsSuccessful.Should().BeFalse();
        result.FailingTypeNames.Should().Contain(t => t.Contains(nameof(OwnedCollectionDbContext), StringComparison.Ordinal));
    }

    [Fact]
    public void TenantModelRule_passes_on_a_context_with_a_table_split_OwnsOne_owned_type()
    {
        var result = TenantModelRule.Evaluate(typeof(TableSplitDbContext).Assembly);

        result.IsSuccessful.Should().BeTrue();
    }

    // Story 1: TenantIdImmutabilityRule.

    [Fact]
    public void TenantIdImmutabilityRule_fails_on_an_entity_with_a_TenantId_setter()
    {
        var result = TenantIdImmutabilityRule.Evaluate(typeof(MutableTenantEntity).Assembly);

        result.IsSuccessful.Should().BeFalse();
        result.FailingTypeNames.Should().Contain(t => t.Contains(nameof(MutableTenantEntity), StringComparison.Ordinal));
    }

    [Fact]
    public void TenantIdImmutabilityRule_passes_on_an_entity_with_a_get_only_TenantId()
    {
        var result = TenantIdImmutabilityRule.Evaluate(typeof(ScopedEntity).Assembly);

        result.IsSuccessful.Should().BeTrue();
    }

    // Story 6 / G4-22-03 / G4-22-04: CrossTenantQueryRule.

    [Fact]
    public void CrossTenantQueryRule_fails_on_an_unattributed_IgnoreQueryFilters_call()
    {
        var result = CrossTenantQueryRule.Evaluate(typeof(UnattributedCrossTenantQuery).Assembly);

        result.IsSuccessful.Should().BeFalse();
        result.FailingTypeNames.Should().Contain(t => t.Contains(nameof(UnattributedCrossTenantQuery), StringComparison.Ordinal));
    }

    [Fact]
    public void CrossTenantQueryRule_fails_on_a_raw_SQL_bypass()
    {
        var result = CrossTenantQueryRule.Evaluate(typeof(RawSqlCrossTenantQuery).Assembly);

        result.IsSuccessful.Should().BeFalse();
        result.FailingTypeNames.Should().Contain(t => t.Contains(nameof(RawSqlCrossTenantQuery), StringComparison.Ordinal));
    }

    [Fact]
    public void CrossTenantQueryRule_fails_on_an_EntityEntry_read_back_bypass()
    {
        var result = CrossTenantQueryRule.Evaluate(typeof(EntryReloadCrossTenantQuery).Assembly);

        result.IsSuccessful.Should().BeFalse();
        result.FailingTypeNames.Should().Contain(t => t.Contains(nameof(EntryReloadCrossTenantQuery), StringComparison.Ordinal));
    }

    [Fact]
    public void CrossTenantQueryRule_fails_on_a_method_group_reference_T10b()
    {
        var result = CrossTenantQueryRule.Evaluate(typeof(MethodGroupCrossTenantQuery).Assembly);

        result.IsSuccessful.Should().BeFalse();
        result.FailingTypeNames.Should().Contain(t => t.Contains(nameof(MethodGroupCrossTenantQuery), StringComparison.Ordinal));
    }

    [Fact]
    public void CrossTenantQueryRule_fails_on_an_expression_tree_reference_T10b()
    {
        var result = CrossTenantQueryRule.Evaluate(typeof(ExpressionTreeCrossTenantQuery).Assembly);

        result.IsSuccessful.Should().BeFalse();
        result.FailingTypeNames.Should().Contain(t => t.Contains(nameof(ExpressionTreeCrossTenantQuery), StringComparison.Ordinal));
    }

    [Fact]
    public void CrossTenantQueryRule_fails_on_a_user_declared_nested_helper_and_names_it_not_the_attributed_outer_type()
    {
        var result = CrossTenantQueryRule.Evaluate(typeof(NestedHelperCrossTenantQuery).Assembly);

        result.IsSuccessful.Should().BeFalse();
        // NetArchTest's own TestResult reports Mono.Cecil's nested-type separator ('/'), not
        // .NET reflection's ('+'); either way, the credited type is Helper itself, never the
        // correctly attributed outer type — T-10a's actual requirement.
        result.FailingTypeNames.Should().Contain(t =>
            t.Contains(nameof(NestedHelperCrossTenantQuery), StringComparison.Ordinal) &&
            t.Contains(nameof(NestedHelperCrossTenantQuery.Helper), StringComparison.Ordinal));
        result.FailingTypeNames.Should().NotContain(nameof(NestedHelperCrossTenantQuery));
    }

    [Fact]
    public void CrossTenantQueryRule_passes_on_a_call_wrapped_in_async_plus_lambda_credited_to_the_outer_type()
    {
        var result = CrossTenantQueryRule.Evaluate(typeof(AttributedCrossTenantQuery).Assembly);

        result.IsSuccessful.Should().BeTrue();
    }

    // ADR-0012 / G4-23-01 and G3 S-1: minting a tenant scope is a bypass too.

    [Theory]
    [InlineData(nameof(UnattributedTenantScope))]
    [InlineData(nameof(UnattributedClaimScope))]
    [InlineData(nameof(UnattributedNoTenantMint))]
    public void CrossTenantQueryRule_fails_on_an_unattributed_tenant_scope_mint(string fixtureName)
    {
        var result = CrossTenantQueryRule.Evaluate(typeof(UnattributedTenantScope).Assembly);

        result.IsSuccessful.Should().BeFalse();
        result.FailingTypeNames.Should().Contain(t => t.Contains(fixtureName, StringComparison.Ordinal));
    }

    [Fact]
    public void CrossTenantQueryRule_passes_on_an_attributed_tenant_scope_mint()
    {
        var result = CrossTenantQueryRule.Evaluate(typeof(AttributedTenantScope).Assembly);

        result.IsSuccessful.Should().BeTrue();
    }

    // Story 6 scenario 3: AllowCrossTenantJustificationRule.

    [Fact]
    public void AllowCrossTenantJustificationRule_fails_on_a_blank_justification()
    {
        var result = AllowCrossTenantJustificationRule.Evaluate(typeof(BlankJustificationQuery).Assembly);

        result.IsSuccessful.Should().BeFalse();
        result.FailingTypeNames.Should().Contain(t => t.Contains(nameof(BlankJustificationQuery), StringComparison.Ordinal));
    }

    [Fact]
    public void AllowCrossTenantJustificationRule_passes_on_a_real_justification()
    {
        var result = AllowCrossTenantJustificationRule.Evaluate(typeof(AttributedCrossTenantQuery).Assembly);

        result.IsSuccessful.Should().BeTrue();
    }

    [Fact]
    public void AllowCrossTenant_usages_are_discoverable_by_reflection_Story6Scenario4()
    {
        var attribute = typeof(AttributedCrossTenantQuery)
            .GetCustomAttributes(typeof(Decisya.SharedKernel.Tenancy.AllowCrossTenantAttribute), inherit: false)
            .Cast<Decisya.SharedKernel.Tenancy.AllowCrossTenantAttribute>()
            .Single();

        attribute.Justification.Should().Be("reconciliation report for platform support ticket #123");
    }

    // G4-22-05: BulkTenantMoveRule.

    [Fact]
    public void BulkTenantMoveRule_fails_on_ExecuteUpdate_setting_TenantId()
    {
        var result = BulkTenantMoveRule.Evaluate(typeof(BulkTenantMoveQuery).Assembly);

        result.IsSuccessful.Should().BeFalse();
        result.FailingTypeNames.Should().Contain(t => t.Contains(nameof(BulkTenantMoveQuery), StringComparison.Ordinal));
    }

    [Fact]
    public void BulkTenantMoveRule_passes_on_ExecuteUpdate_setting_an_ordinary_column()
    {
        var result = BulkTenantMoveRule.Evaluate(typeof(SafeBulkUpdateQuery).Assembly);

        result.IsSuccessful.Should().BeTrue();
    }

    // [AllowCrossTenant] never exempts a bulk tenant move (G4-22-05): the attributed nested
    // helper fixture also calls IgnoreQueryFilters, but never SetProperty(TenantId), so it
    // never trips this rule either way; there is no fixture that combines both, because
    // G4-22-05 explicitly has no escape hatch to prove one against.
}
