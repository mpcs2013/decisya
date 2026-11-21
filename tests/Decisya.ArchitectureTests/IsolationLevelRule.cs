using System.Reflection;
using Mono.Cecil;
using NetArchTest.Rules;

namespace Decisya.ArchitectureTests;

/// <summary>
/// Issue #341, G2 R-L3 (second half): no type in the scope references a <c>BeginTransaction</c> or
/// <c>BeginTransactionAsync</c> overload that takes a <c>System.Data.IsolationLevel</c>. The Ledger's account
/// limit (D2) depends on the default READ COMMITTED level, so a handler must never choose another one.
/// </summary>
public static class IsolationLevelRule
{
    private const string Facade = "Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade";
    private const string FacadeExtensions = "Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions";
    private const string IsolationLevelType = "System.Data.IsolationLevel";

    public static NetArchTest.Rules.TestResult Evaluate(params Assembly[] assemblies) => EvaluateIn(null, assemblies);

    /// <summary>The rule over the types in <paramref name="namespacePrefix"/> (any when <see langword="null"/>); used by the fixture tests.</summary>
    public static NetArchTest.Rules.TestResult EvaluateIn(string? namespacePrefix, params Assembly[] assemblies)
    {
        var types = Types.InAssemblies(assemblies);

        var scoped = namespacePrefix is null
            ? types.Should()
            : types.That().ResideInNamespace(namespacePrefix).Should();

        return scoped.MeetCustomRule(new UsesNoIsolationLevel()).GetResult();
    }

    private sealed class UsesNoIsolationLevel : ICustomRule
    {
        public bool MeetsRule(TypeDefinition type)
        {
            foreach (var reference in MethodReferenceScan.ReferencesOf(type))
            {
                var declaring = reference.DeclaringType.FullName;
                if (declaring is not (Facade or FacadeExtensions)
                    || reference.Name is not ("BeginTransaction" or "BeginTransactionAsync"))
                {
                    continue;
                }

                if (reference.Parameters.Any(p => p.ParameterType.FullName == IsolationLevelType))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
