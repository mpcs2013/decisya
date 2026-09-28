using Decisya.SharedKernel.Tenancy;

namespace Decisya.Infrastructure.Persistence.Tests;

/// <summary>
/// A minimal tenant-scoped aggregate used only to exercise <see cref="TenantDbContext"/>
/// against a real Postgres database (issue #22, G4-22-01 to G4-22-03). Follows the same
/// materialization idiom every <see cref="ITenantScoped"/> entity in this codebase uses: a
/// private parameterless constructor for EF Core (which then writes every property's
/// compiler-generated backing field directly, per <see cref="ITenantScoped"/>'s remarks) and
/// a public constructor for callers. <c>TenantId</c> is a get-only auto-property with no
/// setter of any kind.
/// </summary>
public sealed class TenantScopedProbe : ITenantScoped
{
    private TenantScopedProbe()
    {
        // EF Core materialization constructor.
    }

    /// <summary>
    /// <paramref name="tenantId"/> is accepted exactly as given, including
    /// <c>default(TenantId)</c>: <c>Guid.CreateVersion7()</c> never throws regardless,
    /// which is what lets a test reach <see cref="TenantDbContext"/>'s
    /// <c>UntenantedEntity</c> guard through this constructor.
    /// </summary>
    public TenantScopedProbe(TenantId tenantId, string name)
    {
        Id = Guid.CreateVersion7();
        TenantId = tenantId;
        Name = name;
    }

    /// <summary>
    /// Builds a detached stand-in for an already-persisted row, carrying an id that was not
    /// minted by this constructor (issue #22, G4-22-02/G4-22-03): a test attaches this under
    /// the wrong tenant to prove <see cref="TenantDbContext"/>'s concurrency token turns a
    /// detached cross-tenant <c>Update</c>/<c>Remove</c> into a
    /// <see cref="Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException"/> instead of a
    /// silent cross-tenant write, and to attach a stub with another tenant's id for the
    /// <c>GetDatabaseValues</c>/<c>Reload</c> read-back tests. Internal: not part of the
    /// documented public construction shape, only a test technique.
    /// </summary>
    internal TenantScopedProbe(Guid id, TenantId tenantId, string name)
    {
        Id = id;
        TenantId = tenantId;
        Name = name;
    }

    public Guid Id { get; private set; }

    public TenantId TenantId { get; }

    public string Name { get; private set; } = string.Empty;
}
