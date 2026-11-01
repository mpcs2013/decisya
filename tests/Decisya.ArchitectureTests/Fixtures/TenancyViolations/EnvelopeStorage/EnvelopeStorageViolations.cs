using System.Reflection;
using Decisya.Infrastructure.Persistence;
using Decisya.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Wolverine;

namespace Decisya.ArchitectureTests.Fixtures.TenancyViolations.EnvelopeStorage;

// R7 red fixtures (#231, ADR-0024 point 1; G1 Story 4 scenarios 3 and 4).

/// <summary>
/// Maps <c>Wolverine.Envelope</c>, an entity that is not <c>ITenantScoped</c>, into a
/// <c>TenantDbContext</c> model: <c>TenantModelRule</c> must fail and name this context, and building the
/// model must throw a <c>TenantIsolationException</c> that names <c>Wolverine.Envelope</c>. Every property
/// but the key is ignored so the model reaches the tenant check before EF Core's own validation.
/// </summary>
public sealed class EnvelopeMappingDbContext(DbContextOptions<EnvelopeMappingDbContext> options, ICurrentTenant currentTenant)
    : TenantDbContext(options, currentTenant)
{
    protected override void OnTenantModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        var envelope = modelBuilder.Entity<Envelope>();
        foreach (var property in typeof(Envelope).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!string.Equals(property.Name, nameof(Envelope.Id), StringComparison.Ordinal))
            {
                envelope.Ignore(property.Name);
            }
        }

        envelope.HasKey(nameof(Envelope.Id));
    }
}

/// <summary>
/// A stand-in with the banned name. The real <c>MapWolverineEnvelopeStorage</c> lives in
/// <c>WolverineFx.EntityFrameworkCore</c>, which is not a dependency (ADR-0024 point 2); the rule matches by
/// method name, so a call to this one proves it detects.
/// </summary>
public static class StandInEnvelopeStorageExtensions
{
    public static ModelBuilder MapWolverineEnvelopeStorage(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        return modelBuilder;
    }
}

/// <summary>Calls the banned method.</summary>
public static class MapsEnvelopeStorage
{
    public static ModelBuilder Apply(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        return modelBuilder.MapWolverineEnvelopeStorage();
    }
}
