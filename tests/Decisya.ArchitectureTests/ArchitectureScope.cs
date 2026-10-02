using System.Reflection;
using Decisya.Infrastructure.Persistence;
using Decisya.Modules.Admin;
using Decisya.Modules.Audit;
using Decisya.Modules.Entitlements;
using Decisya.Modules.Tenancy;

namespace Decisya.ArchitectureTests;

/// <summary>
/// Every assembly the tenancy architecture rules run against. Always includes
/// <c>Decisya.Infrastructure.Persistence</c> itself (so the suite is never vacuously empty),
/// plus every <c>Decisya.Modules.*</c> assembly. #21 appends <c>Decisya.Modules.Tenancy</c>
/// here (module-scaffold step 7; B-4), #23 appends <c>Decisya.Modules.Entitlements</c>, #24 appends <c>Decisya.Modules.Audit</c>, #25 appends <c>Decisya.Modules.Admin</c> (no DbContext; it is in scope so the mint and audit rules cover its endpoints), and every later module appends itself the same way.
/// </summary>
public static class ArchitectureScope
{
    public static Assembly[] Assemblies { get; } =
    [
        typeof(TenantDbContext).Assembly,
        typeof(TenancyModule).Assembly,
        typeof(EntitlementsModule).Assembly,
        typeof(AuditModule).Assembly,
        typeof(AdminModule).Assembly,
    ];
}
