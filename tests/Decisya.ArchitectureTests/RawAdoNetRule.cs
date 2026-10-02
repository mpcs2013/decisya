using System.Reflection;
using Mono.Cecil;
using NetArchTest.Rules;

namespace Decisya.ArchitectureTests;

/// <summary>
/// Issue #24, G3 G4-24-03 (T-05, T-03) and G6-24-01. ADR-0013 hands a live <c>DbTransaction</c> to the
/// Audit writer, which runs with the audited module's database role. This rule keeps that capability
/// from becoming a raw-SQL channel. It is an <b>allow-list</b>: in <see cref="ArchitectureScope"/>, no
/// type, <c>[AllowCrossTenant]</c> or not, may reference any member (method, getter, setter,
/// constructor) of a type in the <c>System.Data</c> or <c>Npgsql</c> namespaces, except the members in
/// <see cref="AllowedMembers"/>. That covers commands, batches, readers, data adapters, the Npgsql COPY
/// API, data sources, and a transaction's lifecycle, including ones reached by a cast of
/// <c>transaction.Connection</c>. Anything new fails red and has to be reviewed.
/// The Npgsql EF Core provider (<c>Npgsql.EntityFrameworkCore.*</c>) is not raw ADO.NET and is exempt.
/// The migrator is outside the scope.
/// </summary>
public static class RawAdoNetRule
{
    /// <summary>
    /// What the scope uses today: reading <c>DbTransaction.Connection</c> (the writer) and the
    /// <c>DbException.SqlState</c> (as the <c>PostgresException</c> pattern reads it) and <c>PostgresException.ConstraintName</c> (<c>UniqueViolation</c>, <c>TenantMembershipGate</c>).
    /// </summary>
    internal static readonly IReadOnlySet<(string DeclaringType, string MemberName)> AllowedMembers = new HashSet<(string, string)>
    {
        ("System.Data.Common.DbTransaction", "get_Connection"),
        ("System.Data.Common.DbException", "get_SqlState"),
        ("Npgsql.PostgresException", "get_ConstraintName"),
    };

    public static NetArchTest.Rules.TestResult Evaluate(params Assembly[] assemblies) =>
        Types.InAssemblies(assemblies)
            .Should()
            .MeetCustomRule(new UsesNoRawAdoNet())
            .GetResult();

    internal static bool IsBanned(MethodReference reference)
    {
        var declaring = reference.DeclaringType.FullName;

        var inRawNamespace =
            declaring.StartsWith("System.Data.", StringComparison.Ordinal)
            || (declaring.StartsWith("Npgsql.", StringComparison.Ordinal)
                && !declaring.StartsWith("Npgsql.EntityFrameworkCore.", StringComparison.Ordinal));

        return inRawNamespace && !AllowedMembers.Contains((declaring, reference.Name));
    }

    private sealed class UsesNoRawAdoNet : ICustomRule
    {
        public bool MeetsRule(TypeDefinition type) => !MethodReferenceScan.ReferencesOf(type).Any(IsBanned);
    }
}
