using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using NetArchTest.Rules;

namespace Decisya.ArchitectureTests;

/// <summary>
/// Issue #22, Story 6; G4-22-03 and G4-22-04. A type that references any method on
/// <see cref="BypassList"/> — by <c>call</c>, <c>callvirt</c>, <c>newobj</c>, <c>ldftn</c>,
/// <c>ldvirtftn</c> or <c>ldtoken</c>, so a method-group or an expression-tree reference is
/// caught as well as a direct call — is compliant only if the credited type (the nearest
/// non-compiler-generated ancestor of the type where the reference appears; see
/// <see cref="CompilerGeneratedTypeWalk"/>) carries <c>[AllowCrossTenant]</c>. Whether the
/// justification is non-blank is a separate check: <see cref="AllowCrossTenantJustificationRule"/>.
/// </summary>
public static class CrossTenantQueryRule
{
    /// <summary>
    /// The tenant-filter bypass list (G4-22-03). Every overload of each of these methods is
    /// treated exactly like <c>IgnoreQueryFilters</c>, matched by declaring type full name and
    /// method name only, so arity and generic arguments never matter.
    /// </summary>
    internal static readonly IReadOnlySet<(string DeclaringType, string MethodName)> BypassList = new HashSet<(string, string)>
    {
        ("Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions", "IgnoreQueryFilters"),
        ("Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions", "ExecuteSql"),
        ("Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions", "ExecuteSqlAsync"),
        ("Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions", "ExecuteSqlInterpolated"),
        ("Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions", "ExecuteSqlInterpolatedAsync"),
        ("Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions", "ExecuteSqlRaw"),
        ("Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions", "ExecuteSqlRawAsync"),
        ("Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions", "SqlQuery"),
        ("Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions", "SqlQueryRaw"),
        ("Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions", "GetDbConnection"),
        ("Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry", "GetDatabaseValues"),
        ("Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry", "GetDatabaseValuesAsync"),
        ("Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry", "Reload"),
        ("Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry", "ReloadAsync"),
        // ADR-0012 (#23): minting a tenant scope is a bypass too. Only an [AllowCrossTenant]
        // type may open a context for an explicit tenant or mint the admin precondition (S-1).
        ("Decisya.SharedKernel.Tenancy.TenantResolution", "For"),
        ("Decisya.SharedKernel.Tenancy.TenantResolution", "FromClaim"),
        ("Decisya.SharedKernel.Tenancy.TenantResolution", "get_NoTenant"),
    };

    private static readonly OpCode[] MethodReferencingOpCodes =
    [
        OpCodes.Call, OpCodes.Callvirt, OpCodes.Newobj, OpCodes.Ldftn, OpCodes.Ldvirtftn, OpCodes.Ldtoken,
    ];

    public static NetArchTest.Rules.TestResult Evaluate(params Assembly[] assemblies) =>
        Types.InAssemblies(assemblies)
            .Should()
            .MeetCustomRule(new CreditedTypeCarriesAllowCrossTenant())
            .GetResult();

    private sealed class CreditedTypeCarriesAllowCrossTenant : ICustomRule
    {
        public bool MeetsRule(TypeDefinition type)
        {
            foreach (var (containingType, method) in CompilerGeneratedTypeWalk.MethodsIncludingCompilerGeneratedDescendants(type))
            {
                if (!method.HasBody || !method.Body.Instructions.Any(IsBypassReference))
                {
                    continue;
                }

                var credited = CompilerGeneratedTypeWalk.CreditedType(containingType);

                var isAttributed = credited.CustomAttributes.Any(a =>
                    a.AttributeType.FullName == "Decisya.SharedKernel.Tenancy.AllowCrossTenantAttribute");

                if (!isAttributed)
                {
                    return false;
                }
            }

            return true;
        }
    }

    private static bool IsBypassReference(Instruction instruction)
    {
        if (!MethodReferencingOpCodes.Contains(instruction.OpCode))
        {
            return false;
        }

        var methodReference = instruction.Operand switch
        {
            GenericInstanceMethod generic => generic.ElementMethod,
            MethodReference method => method,
            _ => null,
        };

        return methodReference is not null &&
            BypassList.Contains((methodReference.DeclaringType.FullName, methodReference.Name));
    }
}
