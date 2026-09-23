using System.Reflection;
using NetArchTest.Rules;

namespace Decisya.SharedKernel.Tests.Architecture;

/// <summary>
/// Story 4, Scenario 3 — no module bypasses <c>NodaTime.IClock</c> to reach NodaTime's
/// system-clock singleton directly.
///
/// This is a reusable NetArchTest rule, not a SharedKernel production type: it isn't
/// shipped in the SharedKernel library (a production library has no business depending
/// on NetArchTest.Rules). Once the module-scaffold skill produces real
/// <c>Decisya.Modules.*</c> assemblies, each module's own test project should call
/// <see cref="Evaluate"/> against its own compiled assembly — the natural long-term
/// home for this is a shared <c>Decisya.TestInfrastructure</c> project (see
/// module-scaffold's reference to that project), which does not exist yet as of 0.04.
/// Until then, <see cref="Architecture.ModuleClockUsageRuleTests"/> proves this rule's
/// detection logic against a real "red" fixture (a project that violates the rule) and
/// a real "green" fixture (a project that doesn't), rather than trivially passing
/// because no modules exist yet.
/// </summary>
public static class ModuleClockUsageRule
{
    /// <summary>
    /// Asserts that no type in <paramref name="moduleAssemblies"/> depends on
    /// <c>NodaTime.SystemClock</c>. <paramref name="moduleAssemblies"/> must never
    /// include <c>Decisya.SharedKernel</c> itself, which is the one place allowed to
    /// touch <c>NodaTime.SystemClock</c> (see <c>ClockServiceCollectionExtensions</c>).
    /// </summary>
    public static NetArchTest.Rules.TestResult Evaluate(params Assembly[] moduleAssemblies) =>
        Types.InAssemblies(moduleAssemblies)
            .Should()
            .NotHaveDependencyOn("NodaTime.SystemClock")
            .GetResult();
}
