using Decisya.Infrastructure.Persistence;
using Decisya.SharedKernel.Tenancy;

namespace Decisya.TestInfrastructure;

/// <summary>
/// A settable <see cref="ICurrentTenant"/> for tests (issue #22). Defaults to
/// <see cref="TenantResolution.Invalid"/> (<c>default(TenantResolution)</c>), the same
/// fail-closed default the production implementation carries before anything sets it (#21,
/// G3 boundary B-1) — a test that forgets to set <see cref="Resolution"/> gets an exception,
/// never a resolution that quietly behaves like "no tenant" or a specific tenant.
/// </summary>
public sealed class TestCurrentTenant : ICurrentTenant
{
    /// <summary>
    /// The ambient tenant this instance currently resolves to. <see cref="TenantDbContext"/>
    /// (Decisya.Infrastructure.Persistence) reads this fresh on every query execution and every
    /// save, so switching it between two queries on the same context proves the tenant filter
    /// is never cached into the compiled model.
    /// </summary>
    public TenantResolution Resolution { get; set; }
}
