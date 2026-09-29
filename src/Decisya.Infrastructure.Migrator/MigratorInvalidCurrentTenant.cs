using Decisya.SharedKernel.Tenancy;

namespace Decisya.Infrastructure.Migrator;

/// <summary>
/// The ambient tenant the migrator builds every module's <c>TenantDbContext</c> with (issue
/// #21, G2): always <see cref="TenantResolution.Invalid"/>. DDL (<c>Database.MigrateAsync</c>)
/// runs no filtered query, so this is safe — the same technique <c>PostgresFixture</c> and
/// <c>TenantModelRule</c> use.
/// </summary>
internal sealed class MigratorInvalidCurrentTenant : ICurrentTenant
{
    public TenantResolution Resolution => TenantResolution.Invalid;
}
