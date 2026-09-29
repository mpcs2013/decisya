using System.Reflection;
using NetArchTest.Rules;

namespace Decisya.Modules.Tenancy.Tests;

[Trait("Category", "Architecture")]
public class TenancyModuleBoundaryTests
{
    private static readonly Assembly ModuleAssembly = typeof(TenancyModule).Assembly;

    [Fact]
    public void The_Tenancy_module_references_no_other_modules_implementation()
    {
        var otherModules = ModuleAssembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(n => n.StartsWith("Decisya.Modules.", StringComparison.Ordinal))
            .Where(n => !n.EndsWith(".Contracts", StringComparison.Ordinal))
            .Where(n => n != ModuleAssembly.GetName().Name);

        otherModules.Should().BeEmpty();
    }

    [Fact]
    public void The_Tenancy_module_never_reads_the_system_clock_directly()
    {
        var result = Types.InAssembly(ModuleAssembly)
            .Should()
            .NotHaveDependencyOn("NodaTime.SystemClock")
            .GetResult();

        result.IsSuccessful.Should().BeTrue();
    }

    /// <summary>
    /// G2's remaining module boundary rule: "Only Decisya.Modules.Tenancy.Endpoints depends on
    /// Microsoft.AspNetCore" (issue #21). Domain, Application and Infrastructure stay free of
    /// the web framework; only the minimal-API mapping, the membership middleware and the
    /// authorization policy/handler in Endpoints/ may reference it.
    /// </summary>
    /// <remarks>
    /// G6 note: G2's own architecture doc states the rule as "only Endpoints/", but
    /// <c>TenancyModule</c> — the module's composition root, in the assembly's root namespace —
    /// necessarily exposes <c>IApplicationBuilder</c>/<c>IEndpointRouteBuilder</c>/
    /// <c>IAuthorizationHandler</c>-typed members so <c>Decisya.Api</c>'s <c>Program.cs</c> can
    /// call <c>UseTenancyMembership</c>/<c>MapTenancyEndpoints</c>/<c>AddTenancyModule</c>, the
    /// same way every host's own <c>Program.cs</c> is itself exempt from an analogous rule
    /// elsewhere. This rule therefore excludes exactly that one composition-root type by name,
    /// in addition to Endpoints/, rather than failing red against a reasonable, unavoidable
    /// design. Flagged for G6/architect to reconcile the doc wording; not a product change.
    /// </remarks>
    [Fact]
    public void Only_Endpoints_and_the_composition_root_depend_on_AspNetCore()
    {
        var result = Types.InAssembly(ModuleAssembly)
            .That()
            .DoNotResideInNamespace("Decisya.Modules.Tenancy.Endpoints")
            .And()
            .DoNotHaveName("TenancyModule")
            .ShouldNot()
            .HaveDependencyOn("Microsoft.AspNetCore")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            string.Join(", ", result.FailingTypeNames ?? []));
    }
}
