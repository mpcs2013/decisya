using System.Reflection;
using Decisya.Infrastructure.Persistence;
using Decisya.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Mono.Cecil;
using NetArchTest.Rules;

namespace Decisya.ArchitectureTests;

/// <summary>
/// Issue #22, Story 7 (the Done-when rule). Every non-abstract <see cref="TenantDbContext"/>
/// subclass is built through the constructor convention and its model is read, with the
/// ambient tenant <see cref="TenantResolution.Invalid"/> and a connection string that is
/// never dialled (building the model never opens a connection): a missing constructor, a
/// <see cref="TenantIsolationException"/> raised while building the model (an entity type that
/// is not <see cref="ITenantScoped"/>), a root entity type without the named
/// <see cref="TenantDbContext.TenantFilterName"/> filter, or a <c>TenantId</c> property that is
/// not a concurrency token, each fail this rule and name the context type.
/// </summary>
public static class TenantModelRule
{
    private const string ModelOnlyConnectionString =
        "Host=model-only.invalid;Database=architecture-tests;Username=architecture-tests;Password=architecture-tests";

    public static NetArchTest.Rules.TestResult Evaluate(params Assembly[] assemblies) =>
        Types.InAssemblies(assemblies)
            .That()
            .Inherit(typeof(TenantDbContext))
            .And()
            .AreNotAbstract()
            .Should()
            .MeetCustomRule(new BuildsAValidTenantModel(assemblies))
            .GetResult();

    private sealed class BuildsAValidTenantModel(Assembly[] assemblies) : ICustomRule
    {
        public bool MeetsRule(TypeDefinition type)
        {
            var runtimeType = ResolveRuntimeType(type.FullName.Replace('/', '+'));
            if (runtimeType is null)
            {
                return false;
            }

            var optionsType = typeof(DbContextOptions<>).MakeGenericType(runtimeType);
            var constructor = runtimeType.GetConstructor([optionsType, typeof(ICurrentTenant)]);
            if (constructor is null)
            {
                return false;
            }

            var options = BuildModelOnlyOptions(runtimeType);
            var context = (DbContext)constructor.Invoke([options, new InvalidCurrentTenant()]);

            try
            {
                return ModelIsValid(context.Model);
            }
            catch (TenantIsolationException)
            {
                return false;
            }
            finally
            {
                context.Dispose();
            }
        }

        private Type? ResolveRuntimeType(string fullName) =>
            assemblies.Select(assembly => assembly.GetType(fullName)).FirstOrDefault(t => t is not null);

        private static object BuildModelOnlyOptions(Type contextType)
        {
            var builderType = typeof(DbContextOptionsBuilder<>).MakeGenericType(contextType);
            var builder = Activator.CreateInstance(builderType)!;

            var useNpgsql = typeof(NpgsqlDbContextOptionsBuilderExtensions)
                .GetMethods()
                .Single(m => m.Name == nameof(NpgsqlDbContextOptionsBuilderExtensions.UseNpgsql) &&
                    m.IsGenericMethodDefinition &&
                    m.GetParameters() is [_, { ParameterType.Name: nameof(String) }, _])
                .MakeGenericMethod(contextType);

            var configured = useNpgsql.Invoke(null, [builder, ModelOnlyConnectionString, null]);

            // DeclaredOnly: DbContextOptionsBuilder<T> hides the non-generic base's Options
            // (via the new keyword) with its own DbContextOptions<T>-typed property of the
            // same name, so an unqualified GetProperty lookup is ambiguous between the two.
            return builderType
                .GetProperty(nameof(DbContextOptionsBuilder<DbContext>.Options), BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)!
                .GetValue(configured)!;
        }

        private static bool ModelIsValid(IModel model)
        {
            foreach (var entityType in model.GetEntityTypes())
            {
                if (entityType.IsOwned())
                {
                    continue;
                }

                var tenantIdProperty = entityType.FindProperty(nameof(ITenantScoped.TenantId));
                if (tenantIdProperty is null || !tenantIdProperty.IsConcurrencyToken)
                {
                    return false;
                }

                if (entityType.BaseType is null &&
                    entityType.FindDeclaredQueryFilter(TenantDbContext.TenantFilterName) is null)
                {
                    return false;
                }
            }

            return true;
        }

        private sealed class InvalidCurrentTenant : ICurrentTenant
        {
            public TenantResolution Resolution => TenantResolution.Invalid;
        }
    }
}
