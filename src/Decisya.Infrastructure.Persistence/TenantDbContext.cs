using System.Reflection;
using Decisya.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Decisya.Infrastructure.Persistence;

/// <summary>
/// The base <see cref="DbContext"/> every tenant-scoped module context derives from (issue
/// #22; CLAUDE.md invariant 1; ADR-0001). Applies a named, per-query tenant filter to every
/// root <see cref="ITenantScoped"/> entity type, verifies every write against the ambient
/// tenant before it reaches the database, and refuses to build a model that maps any type
/// that is not <see cref="ITenantScoped"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Constructor convention.</b> Every concrete subclass must expose a public constructor
/// shaped <c>(DbContextOptions&lt;TSelf&gt; options, ICurrentTenant currentTenant)</c>.
/// <c>PostgresFixture</c> and <c>TenantModelRule</c> both construct contexts through it. The
/// <see cref="ICurrentTenant"/> reference is kept, never copied, so a later change to the
/// ambient resolution (a new request reusing a pooled service provider is not itself
/// supported — see below) is always observed. <c>AddDbContextPool</c> is not compatible with
/// this constructor and must not be worked around.
/// </para>
/// <para>
/// <b>Why the filter is evaluated per query, not cached.</b> EF Core caches the compiled
/// model per context <em>type</em>. A filter lambda that captures <c>this</c> and reads
/// <see cref="CurrentTenantFilter"/> becomes, from EF's perspective, a reference to a member of
/// the executing context instance: EF turns it into a query parameter evaluated fresh for the
/// context instance that runs the query, never a value frozen into the cached model. A local
/// value captured while the model itself is being built would instead be baked in once, for
/// every later context and every tenant — the leak this design rules out.
/// </para>
/// </remarks>
public abstract class TenantDbContext : DbContext
{
    /// <summary>The name of the query filter this context applies to every tenant-scoped entity.</summary>
    public const string TenantFilterName = "Tenant";

    private static readonly MethodInfo ApplyTenantFilterMethod = typeof(TenantDbContext)
        .GetMethod(nameof(ApplyTenantFilter), BindingFlags.Instance | BindingFlags.NonPublic)!;

    private readonly ICurrentTenant _currentTenant;

    protected TenantDbContext(DbContextOptions options, ICurrentTenant currentTenant)
        : base(options)
    {
        ArgumentNullException.ThrowIfNull(currentTenant);
        _currentTenant = currentTenant;
    }

    /// <summary>
    /// The current tenant, read fresh on every access. <c>Tenant</c> resolves to its id;
    /// <c>None</c> resolves to <see langword="null"/> — compared against the non-nullable
    /// <c>tenant_id</c> column, a <see langword="null"/> parameter matches no row, which is
    /// how "no tenant" yields zero rows rather than every tenant's rows. <c>Invalid</c> throws
    /// while EF extracts the query's parameter values, before any SQL is sent.
    /// </summary>
    private TenantId? CurrentTenantFilter =>
        _currentTenant.Resolution.Kind switch
        {
            TenantResolutionKind.Tenant => _currentTenant.Resolution.TenantId,
            TenantResolutionKind.None => null,
            _ => throw new TenantIsolationException(TenantIsolationViolation.InvalidTenant, GetType().FullName ?? GetType().Name),
        };

    /// <summary>
    /// A derived context configures its own entities, schema and conventions here — never in
    /// <see cref="OnModelCreating"/>, which is sealed so every subclass goes through the
    /// tenancy checks and the filter below.
    /// </summary>
    protected abstract void OnTenantModelCreating(ModelBuilder modelBuilder);

    /// <summary>
    /// Calls <see cref="OnTenantModelCreating"/> first, then walks every mapped entity type. An
    /// owned type must share its owner's table and schema (table splitting) or be mapped with
    /// <c>ToJson</c> — the only shapes that carry no <c>tenant_id</c> of their own without a
    /// separate table a plain key-based write could reach (<see cref="TenantIsolationViolation.OwnedTypeNotInOwnersTable"/>;
    /// issue #22, G6-22-01). A non-owned entity type that is not <see cref="ITenantScoped"/>
    /// fails the whole model build (<see cref="TenantIsolationViolation.UnscopedEntityType"/>);
    /// otherwise its <c>TenantId</c> property becomes required and a concurrency token, and —
    /// on root entity types only, as EF requires for a filter on a hierarchy — the named
    /// <see cref="TenantFilterName"/> filter is added.
    /// </summary>
    protected sealed override void OnModelCreating(ModelBuilder modelBuilder)
    {
        OnTenantModelCreating(modelBuilder);

        List<string>? unscopedTypeNames = null;
        List<string>? misplacedOwnedTypeNames = null;

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (entityType.IsOwned())
            {
                if (!OwnedTypeSharesOwnersStorage(entityType))
                {
                    (misplacedOwnedTypeNames ??= []).Add(entityType.ClrType.FullName ?? entityType.ClrType.Name);
                }

                continue;
            }

            var clrType = entityType.ClrType;

            if (!typeof(ITenantScoped).IsAssignableFrom(clrType))
            {
                (unscopedTypeNames ??= []).Add(clrType.FullName ?? clrType.Name);
                continue;
            }

            modelBuilder.Entity(clrType)
                .Property(nameof(ITenantScoped.TenantId))
                .IsRequired()
                .IsConcurrencyToken();

            if (entityType.BaseType is null)
            {
                ApplyTenantFilterMethod.MakeGenericMethod(clrType).Invoke(this, [modelBuilder]);
            }
        }

        if (unscopedTypeNames is { Count: > 0 })
        {
            throw new TenantIsolationException(TenantIsolationViolation.UnscopedEntityType, unscopedTypeNames);
        }

        if (misplacedOwnedTypeNames is { Count: > 0 })
        {
            throw new TenantIsolationException(TenantIsolationViolation.OwnedTypeNotInOwnersTable, misplacedOwnedTypeNames);
        }
    }

    /// <summary>
    /// <see langword="true"/> for an owned type mapped with <c>ToJson</c> (embedded as a column
    /// on the owner's row, so it needs no <c>tenant_id</c> of its own), or for one whose table
    /// and schema are identical to its ownership principal's (table splitting: it shares the
    /// owner's row, and therefore the owner's <c>tenant_id</c>, one-for-one). Anything else —
    /// <c>OwnsMany</c>, or <c>OwnsOne(...).ToTable(...)</c> — gets a table of its own with no
    /// <c>tenant_id</c> column, reachable by key alone once its owner is merely attached, never
    /// read through the filter (G6-22-01).
    /// </summary>
    private static bool OwnedTypeSharesOwnersStorage(Microsoft.EntityFrameworkCore.Metadata.IReadOnlyEntityType entityType)
    {
        if (entityType.IsMappedToJson())
        {
            return true;
        }

        var owner = entityType.FindOwnership()?.PrincipalEntityType;

        return owner is not null &&
            entityType.GetTableName() == owner.GetTableName() &&
            entityType.GetSchema() == owner.GetSchema();
    }

    /// <summary>
    /// Built through reflection (<see cref="ApplyTenantFilterMethod"/>) for each concrete
    /// tenant-scoped root entity type, so the filter lambda below closes over <c>this</c> and
    /// therefore reads <see cref="CurrentTenantFilter"/> fresh on every query execution.
    /// </summary>
    private void ApplyTenantFilter<TEntity>(ModelBuilder modelBuilder)
        where TEntity : class, ITenantScoped
    {
        modelBuilder.Entity<TEntity>().HasQueryFilter(TenantFilterName, e => e.TenantId == CurrentTenantFilter);
    }

    /// <summary>Registers <see cref="TenantIdValueConverter"/> for every <see cref="TenantId"/> property. An override that skips <c>base</c> leaves <c>TenantId</c> unmappable, which fails the model build — also fail-closed.</summary>
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);
        configurationBuilder.Properties<TenantId>().HaveConversion<TenantIdValueConverter>();
    }

    /// <summary>Runs the tenant isolation guard, then persists. See the type-level remarks and the guard table in <see cref="EnforceTenantIsolation"/>.</summary>
    public sealed override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnforceTenantIsolation();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    /// <summary>Runs the tenant isolation guard, then persists. See the type-level remarks and the guard table in <see cref="EnforceTenantIsolation"/>.</summary>
    public sealed override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        EnforceTenantIsolation();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>
    /// Checked before any change is persisted (issue #22, Story 5; G1 open question 2: this
    /// throws rather than returning a <c>Result</c>, because it guards against a programming
    /// error, not an expected business outcome):
    /// <list type="table">
    /// <listheader><term>Condition</term><description>Violation</description></listheader>
    /// <item><term>ambient resolution is <c>Invalid</c></term><description><see cref="TenantIsolationViolation.InvalidTenant"/></description></item>
    /// <item><term>ambient resolution is <c>None</c>, and a tenant-scoped entity is being written</term><description><see cref="TenantIsolationViolation.NoTenant"/></description></item>
    /// <item><term>an added entity's <c>TenantId</c> is not initialized</term><description><see cref="TenantIsolationViolation.UntenantedEntity"/></description></item>
    /// <item><term>an added entity's <c>TenantId</c>, or an existing entity's original <c>TenantId</c>, does not match the ambient tenant</term><description><see cref="TenantIsolationViolation.TenantMismatch"/></description></item>
    /// <item><term>an existing entity's <c>TenantId</c> was changed</term><description><see cref="TenantIsolationViolation.TenantChanged"/></description></item>
    /// </list>
    /// Throws on the first violation found, before <c>base.SaveChanges</c> runs, so no SQL is
    /// sent. <c>ChangeTracker.DetectChanges()</c> runs explicitly first, so the guard still
    /// sees a change made with <c>ChangeTracker.AutoDetectChangesEnabled</c> off (for example,
    /// through an entry's property <c>CurrentValue</c>, the only way to change <c>TenantId</c>
    /// given it has no setter).
    /// </summary>
    private void EnforceTenantIsolation()
    {
        var resolution = _currentTenant.Resolution;

        if (resolution.Kind == TenantResolutionKind.Invalid)
        {
            throw new TenantIsolationException(TenantIsolationViolation.InvalidTenant, GetType().FullName ?? GetType().Name);
        }

        ChangeTracker.DetectChanges();

        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.Entity is not ITenantScoped ||
                entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
            {
                continue;
            }

            var entityTypeName = entry.Entity.GetType().FullName ?? entry.Entity.GetType().Name;

            if (resolution.Kind == TenantResolutionKind.None)
            {
                throw new TenantIsolationException(TenantIsolationViolation.NoTenant, entityTypeName);
            }

            var currentTenantId = resolution.TenantId;

            if (entry.State == EntityState.Added)
            {
                var tenantId = ((ITenantScoped)entry.Entity).TenantId;

                if (!tenantId.IsInitialized)
                {
                    throw new TenantIsolationException(TenantIsolationViolation.UntenantedEntity, entityTypeName);
                }

                if (tenantId != currentTenantId)
                {
                    throw new TenantIsolationException(TenantIsolationViolation.TenantMismatch, entityTypeName);
                }

                continue;
            }

            var originalTenantId = (TenantId)entry.Property(nameof(ITenantScoped.TenantId)).OriginalValue!;

            if (originalTenantId != currentTenantId)
            {
                throw new TenantIsolationException(TenantIsolationViolation.TenantMismatch, entityTypeName);
            }

            if (entry.State == EntityState.Modified)
            {
                var currentValue = (TenantId)entry.Property(nameof(ITenantScoped.TenantId)).CurrentValue!;

                if (currentValue != originalTenantId)
                {
                    throw new TenantIsolationException(TenantIsolationViolation.TenantChanged, entityTypeName);
                }
            }
        }
    }
}
