using System.Reflection;
using Mono.Cecil;
using NetArchTest.Rules;

namespace Decisya.ArchitectureTests;

/// <summary>
/// Issue #24, ADR-0013 (G2) and G6-24-02. Transaction control is reviewed code, per type and per member:
/// <list type="bullet">
/// <item><description>Transaction-control members are referenced only by the types on the allow-list
/// (<see cref="AllowedTypes"/>), and each listed type may use only its own members. The three Entitlements
/// handlers may use <c>BeginTransaction(Async)</c> and <c>IDbContextTransaction.Commit*</c>/<c>Rollback*</c>;
/// the Audit writer may use <c>UseTransaction(Async)</c> only, so it can neither start, commit nor roll back
/// the caller's transaction, through EF or otherwise (<see cref="RawAdoNetRule"/> covers ADO.NET).</description></item>
/// <item><description>The controlled members are <c>BeginTransaction(Async)</c>, <c>UseTransaction(Async)</c>,
/// <c>EnlistTransaction</c>, <c>CommitTransaction(Async)</c> and <c>RollbackTransaction(Async)</c> on
/// <c>DatabaseFacade</c> and its relational extensions, and <c>Commit</c>, <c>CommitAsync</c>, <c>Rollback</c>
/// and <c>RollbackAsync</c> on <c>IDbContextTransaction</c>.</description></item>
/// <item><description>No type at all, listed or not, sets <c>DatabaseFacade.AutoSavepointsEnabled</c> or
/// <c>AutoTransactionBehavior</c>, or calls <c>IDbContextTransaction</c> <c>CreateSavepoint*</c>,
/// <c>RollbackToSavepoint*</c> or <c>ReleaseSavepoint*</c>: the #23 race paths (23505, concurrency) depend on EF's
/// automatic savepoints.</description></item>
/// </list>
/// </summary>
public static class ExplicitTransactionRule
{
    private static readonly string[] HandlerMembers =
    [
        "BeginTransaction", "BeginTransactionAsync", "Commit", "CommitAsync", "Rollback", "RollbackAsync",
    ];

    private static readonly string[] WriterMembers = ["UseTransaction", "UseTransactionAsync"];

    /// <summary>The four types that may control a transaction (ADR-0013), each with the members it may use.</summary>
    public static IReadOnlyDictionary<string, string[]> AllowedTypes { get; } = new Dictionary<string, string[]>
    {
        ["Decisya.Modules.Entitlements.Application.StartTrialHandler"] = HandlerMembers,
        ["Decisya.Modules.Entitlements.Application.GrantOverrideHandler"] = HandlerMembers,
        ["Decisya.Modules.Entitlements.Application.RevokeOverrideHandler"] = HandlerMembers,
        [CrossTenantAuditRule.WriterFullName] = WriterMembers,
    };

    private const string Facade = "Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade";
    private const string FacadeExtensions = "Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions";
    private const string EfTransaction = "Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction";

    private static readonly HashSet<string> FacadeControlMethods =
    [
        "BeginTransaction", "BeginTransactionAsync",
        "UseTransaction", "UseTransactionAsync",
        "EnlistTransaction",
        "CommitTransaction", "CommitTransactionAsync",
        "RollbackTransaction", "RollbackTransactionAsync",
    ];

    private static readonly HashSet<string> EfTransactionControlMethods =
    [
        "Commit", "CommitAsync", "Rollback", "RollbackAsync",
    ];

    private static readonly HashSet<string> NeverSetOnFacade =
    [
        "set_AutoSavepointsEnabled", "set_AutoTransactionBehavior",
    ];

    private static readonly HashSet<string> NeverCalledOnEfTransaction =
    [
        "CreateSavepoint", "CreateSavepointAsync",
        "RollbackToSavepoint", "RollbackToSavepointAsync",
        "ReleaseSavepoint", "ReleaseSavepointAsync",
    ];

    public static NetArchTest.Rules.TestResult Evaluate(
        IReadOnlyDictionary<string, string[]> allowedMembersByType, params Assembly[] assemblies)
    {
        return Types.InAssemblies(assemblies)
            .Should()
            .MeetCustomRule(new ControlsTransactionsOnlyAsListed(allowedMembersByType))
            .GetResult();
    }

    private sealed class ControlsTransactionsOnlyAsListed(IReadOnlyDictionary<string, string[]> allowedMembersByType) : ICustomRule
    {
        public bool MeetsRule(TypeDefinition type)
        {
            allowedMembersByType.TryGetValue(MethodReferenceScan.ReflectionName(type), out var allowed);

            foreach (var reference in MethodReferenceScan.ReferencesOf(type))
            {
                var declaring = reference.DeclaringType.FullName;
                var name = reference.Name;

                var onFacade = declaring is Facade or FacadeExtensions;

                if (onFacade && NeverSetOnFacade.Contains(name))
                {
                    return false;
                }

                if (declaring == EfTransaction && NeverCalledOnEfTransaction.Contains(name))
                {
                    return false;
                }

                var controlled =
                    (onFacade && FacadeControlMethods.Contains(name))
                    || (declaring == EfTransaction && EfTransactionControlMethods.Contains(name));

                if (controlled && (allowed is null || !allowed.Contains(name)))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
