using Decisya.Infrastructure.Persistence;
using Decisya.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;

namespace Decisya.ArchitectureTests.Fixtures.TenancyViolations;

/// <summary>
/// G6-22-02 red fixture: <c>ConfigureConventions</c> is <c>protected override</c> but not
/// sealed, so a derived context can add an <see cref="IModelFinalizingConvention"/> that runs
/// after the sealed <c>OnModelCreating</c> and clears <c>TenantId</c>'s concurrency token —
/// exactly the path a detached cross-tenant <c>Update</c>/<c>Remove</c> relies on being closed
/// (T-05). <c>TenantModelRule</c> must catch this even though the token was stripped after the
/// base class configured it, not by skipping the base configuration.
/// </summary>
public sealed class StrippedConcurrencyTokenDbContext(DbContextOptions<StrippedConcurrencyTokenDbContext> options, ICurrentTenant currentTenant)
    : TenantDbContext(options, currentTenant)
{
    protected override void OnTenantModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ScopedProbe>();
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);
        configurationBuilder.Conventions.Add(_ => new StripTenantIdConcurrencyTokenConvention());
    }

    private sealed class StripTenantIdConcurrencyTokenConvention : IModelFinalizingConvention
    {
        public void ProcessModelFinalizing(IConventionModelBuilder modelBuilder, IConventionContext<IConventionModelBuilder> context)
        {
            foreach (var entityType in modelBuilder.Metadata.GetEntityTypes())
            {
                var property = entityType.FindProperty(nameof(ITenantScoped.TenantId));
                if (property is null)
                {
                    continue;
                }

                // The Builder API respects configuration-source precedence (Explicit fluent
                // API, which TenantDbContext.OnModelCreating used, always beats a Convention-
                // or even a DataAnnotation-sourced call), so property.Builder.IsConcurrencyToken(false)
                // is silently a no-op here. IMutableProperty's plain setter has no such
                // precedence tracking and forces the value regardless of who set it last -
                // exactly the "or equivalent" finalizing-convention path G6-22-02 asks for.
                ((IMutableProperty)property).IsConcurrencyToken = false;
            }
        }
    }
}
