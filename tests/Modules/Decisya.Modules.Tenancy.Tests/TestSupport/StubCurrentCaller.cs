using Decisya.SharedKernel.Tenancy;

namespace Decisya.Modules.Tenancy.Tests.TestSupport;

/// <summary>
/// A fixed <see cref="ICurrentCaller"/> for module tests that construct
/// <c>Application.TenantMembershipGate</c> directly (issue #21, G4-21-03), without a host or a
/// real login. Mirrors <c>Decisya.TestInfrastructure.TestCurrentTenant</c>'s role for
/// <see cref="ICurrentTenant"/>.
/// </summary>
internal sealed class StubCurrentCaller(string userId) : ICurrentCaller
{
    public string UserId { get; } = userId;
}
