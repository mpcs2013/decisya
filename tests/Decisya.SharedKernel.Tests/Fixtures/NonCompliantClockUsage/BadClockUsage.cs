using NodaTime;

namespace Decisya.SharedKernel.Tests.Fixtures.NonCompliantClockUsage;

/// <summary>
/// Deliberately violates the "no module bypasses IClock to reach NodaTime's system
/// clock directly" rule (0.04 Story 4) by referencing <see cref="SystemClock"/>
/// directly instead of depending on <see cref="IClock"/> via dependency injection.
/// Exists only so <c>ModuleClockUsageRuleTests</c> can prove the architecture rule
/// actually detects a violation; it is never wired into any real composition root.
/// </summary>
public static class BadClockUsage
{
    public static Instant Now() => SystemClock.Instance.GetCurrentInstant();
}
