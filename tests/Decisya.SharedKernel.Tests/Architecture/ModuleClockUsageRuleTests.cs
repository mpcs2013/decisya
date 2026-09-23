using Decisya.SharedKernel.Tests.Fixtures.NonCompliantClockUsage;

namespace Decisya.SharedKernel.Tests.Architecture;

/// <summary>
/// Proves <see cref="ModuleClockUsageRule"/> actually detects a violation ("red" case,
/// using a fixture project that does call <c>NodaTime.SystemClock</c> directly) and
/// correctly passes compliant code ("green" case, using a real assembly from the
/// solution that never touches it).
/// </summary>
public class ModuleClockUsageRuleTests
{
    [Fact]
    public void Detects_a_module_that_calls_NodaTime_SystemClock_directly()
    {
        var result = ModuleClockUsageRule.Evaluate(typeof(BadClockUsage).Assembly);

        result.IsSuccessful.Should().BeFalse();
        result.FailingTypeNames.Should().Contain(t => t.Contains(nameof(BadClockUsage), StringComparison.Ordinal));
    }

    [Fact]
    public void Passes_for_an_assembly_that_never_touches_NodaTime_SystemClock()
    {
        // Decisya.ServiceDefaults is a real, already-shipped assembly that has no
        // dependency on NodaTime at all, let alone SystemClock.
        var result = ModuleClockUsageRule.Evaluate(typeof(Microsoft.Extensions.Hosting.Extensions).Assembly);

        result.IsSuccessful.Should().BeTrue();
    }
}
