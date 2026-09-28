namespace Decisya.ArchitectureTests;

/// <summary>
/// Runs every tenancy architecture rule against <see cref="ArchitectureScope.Assemblies"/> —
/// the full, real scope, as opposed to <c>TenancyRuleTests</c>, which proves each rule's
/// detection logic against the <c>Fixtures/TenancyViolations</c> and
/// <c>Fixtures/TenancyCompliant</c> fixture projects. <see cref="ArchitectureScope.Assemblies"/>
/// carries no module assemblies as of #22 (module-scaffold's "tenancy" phase, step 7, is what
/// adds one), so every rule here passes vacuously today: there is nothing to violate yet, only
/// <c>Decisya.Infrastructure.Persistence</c> itself, which declares no concrete
/// <c>TenantDbContext</c> and no module <c>Domain</c> code. #21 gives these tests their first
/// real assertions.
/// </summary>
public class ModuleBoundaryTests
{
    [Fact]
    public void TenantModelRule_passes_over_the_current_architecture_scope()
    {
        var result = TenantModelRule.Evaluate(ArchitectureScope.Assemblies);

        result.IsSuccessful.Should().BeTrue();
    }

    [Fact]
    public void TenantIdImmutabilityRule_passes_over_the_current_architecture_scope()
    {
        var result = TenantIdImmutabilityRule.Evaluate(ArchitectureScope.Assemblies);

        result.IsSuccessful.Should().BeTrue();
    }

    [Fact]
    public void CrossTenantQueryRule_passes_over_the_current_architecture_scope()
    {
        var result = CrossTenantQueryRule.Evaluate(ArchitectureScope.Assemblies);

        result.IsSuccessful.Should().BeTrue();
    }

    [Fact]
    public void AllowCrossTenantJustificationRule_passes_over_the_current_architecture_scope()
    {
        var result = AllowCrossTenantJustificationRule.Evaluate(ArchitectureScope.Assemblies);

        result.IsSuccessful.Should().BeTrue();
    }

    [Fact]
    public void BulkTenantMoveRule_passes_over_the_current_architecture_scope()
    {
        var result = BulkTenantMoveRule.Evaluate(ArchitectureScope.Assemblies);

        result.IsSuccessful.Should().BeTrue();
    }
}
