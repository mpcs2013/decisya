using System.Reflection;
using Decisya.SharedKernel.Tenancy;
using Mono.Cecil;
using NetArchTest.Rules;

namespace Decisya.ArchitectureTests;

/// <summary>
/// Issue #24, G2 (C-1 guard) and G3 S-1. Every type carrying <see cref="AllowCrossTenantAttribute"/>
/// must reference <c>IAuditWriter.AppendAsync</c> (async state machines and closures count; see
/// <see cref="MethodReferenceScan"/>), so a cross-tenant admin command cannot ship unaudited.
/// <para>
/// The only exemption is the writer itself: the type <c>Decisya.Modules.Audit.Application.AuditWriter</c>
/// <b>of the <c>Decisya.Modules.Audit</c> assembly</b>. Neither the Audit assembly nor an Audit
/// namespace is exempt (S-1), so #25's audited reader, which belongs in Audit, is held to the rule.
/// A type that merely references <c>AppendAsync</c> still has to be reviewed for appending on every
/// success path and before commit: the fault-matrix tests (G4-24-02) prove that per command.
/// </para>
/// </summary>
public static class CrossTenantAuditRule
{
    internal const string WriterAssembly = "Decisya.Modules.Audit";
    internal const string WriterFullName = "Decisya.Modules.Audit.Application.AuditWriter";
    private const string AuditWriterInterface = "Decisya.Modules.Audit.Contracts.IAuditWriter";
    private const string AppendMethod = "AppendAsync";

    public static NetArchTest.Rules.TestResult Evaluate(params Assembly[] assemblies) => EvaluateIn(null, assemblies);

    /// <summary>The rule over the attributed types in <paramref name="namespacePrefix"/> (any when <see langword="null"/>); used by the fixture tests.</summary>
    public static NetArchTest.Rules.TestResult EvaluateIn(string? namespacePrefix, params Assembly[] assemblies)
    {
        var attributed = Types.InAssemblies(assemblies).That().HaveCustomAttribute(typeof(AllowCrossTenantAttribute));

        var scoped = namespacePrefix is null
            ? attributed.Should()
            : attributed.And().ResideInNamespace(namespacePrefix).Should();

        return scoped.MeetCustomRule(new AppendsAnAuditRecord()).GetResult();
    }

    private sealed class AppendsAnAuditRecord : ICustomRule
    {
        public bool MeetsRule(TypeDefinition type)
        {
            if (type.Module.Assembly.Name.Name == WriterAssembly && MethodReferenceScan.ReflectionName(type) == WriterFullName)
            {
                return true;
            }

            return MethodReferenceScan.ReferencesOf(type).Any(r =>
                r.Name == AppendMethod && r.DeclaringType.FullName == AuditWriterInterface);
        }
    }
}
