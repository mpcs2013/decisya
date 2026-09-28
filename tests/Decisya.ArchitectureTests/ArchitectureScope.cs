using System.Reflection;
using Decisya.Infrastructure.Persistence;

namespace Decisya.ArchitectureTests;

/// <summary>
/// Every assembly the tenancy architecture rules run against. Always includes
/// <c>Decisya.Infrastructure.Persistence</c> itself (so the suite is never vacuously empty),
/// plus every <c>Decisya.Modules.*</c> assembly. Empty of module assemblies as of #22; #21
/// appends <c>Decisya.Modules.Tenancy</c> here (module-scaffold step 7), and every later
/// module appends itself the same way.
/// </summary>
public static class ArchitectureScope
{
    public static Assembly[] Assemblies { get; } = [typeof(TenantDbContext).Assembly];
}
