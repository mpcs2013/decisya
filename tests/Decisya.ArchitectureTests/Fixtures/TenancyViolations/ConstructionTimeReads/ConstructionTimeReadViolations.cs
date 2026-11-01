using Decisya.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;

namespace Decisya.ArchitectureTests.Fixtures.TenancyViolations.ConstructionTimeReads;

// R16 red fixtures (#231, G3 G4-231-09): each reads the tenant or the caller where WolverineFx 6.39.1
// builds the object, before the envelope tenant middleware has set them.

/// <summary>A constructor that reads <c>ICurrentTenant.Resolution</c>: freezes <c>Invalid</c>.</summary>
public sealed class CtorReadsResolution
{
    private readonly TenantResolution _frozen;

    public CtorReadsResolution(ICurrentTenant tenant)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        _frozen = tenant.Resolution;
    }

    public TenantResolution Frozen => _frozen;
}

/// <summary>A constructor that reads <c>ICurrentCaller.UserId</c>: throws before the check on a refused message.</summary>
public sealed class CtorReadsUserId
{
    private readonly string _user;

    public CtorReadsUserId(ICurrentCaller caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        _user = caller.UserId;
    }

    public string User => _user;
}

/// <summary>A field initializer that reads <c>ICurrentCaller.IsPlatformAdmin</c> (it compiles into the constructor).</summary>
public sealed class FieldInitializerReadsIsPlatformAdmin(ICurrentCaller caller)
{
    private readonly bool _admin = caller.IsPlatformAdmin;

    public bool Admin => _admin;
}

/// <summary>A DI factory that reads <c>ICurrentTenant.Resolution</c> when the service is built.</summary>
public static class DiFactoryReadsResolution
{
    public static void Register(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<string>(serviceProvider => serviceProvider.GetRequiredService<ICurrentTenant>().Resolution.ToString());
    }
}
