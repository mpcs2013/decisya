using System.Reflection;
using Decisya.Modules.Admin.Contracts;
using Decisya.Modules.Audit.Contracts;
using Decisya.Modules.Entitlements.Contracts;
using Decisya.Modules.Tenancy.Contracts;

namespace Decisya.ArchitectureTests;

/// <summary>
/// Every <c>Decisya.Modules.*.Contracts</c> assembly (issue #21, G2), enumerated the same way
/// <see cref="ArchitectureScope"/> enumerates implementation assemblies (B-4). #21 adds the
/// first: <c>Decisya.Modules.Tenancy.Contracts</c>; every later module's Contracts assembly is
/// appended here the same way.
/// </summary>
public static class ContractsScope
{
    public static Assembly[] Assemblies { get; } =
    [
        typeof(TenantDto).Assembly,
        typeof(IEntitlementService).Assembly,
        typeof(IAuditWriter).Assembly,
        typeof(OverrideGrantRequest).Assembly,
    ];
}
