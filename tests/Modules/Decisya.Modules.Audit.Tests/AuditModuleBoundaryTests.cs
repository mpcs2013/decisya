using System.Data.Common;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Decisya.Modules.Audit.Contracts;
using Decisya.Modules.Audit.Domain;
using Decisya.Modules.Audit.Infrastructure;
using Decisya.SharedKernel.Observability;
using Decisya.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using NetArchTest.Rules;
using NodaTime;

namespace Decisya.Modules.Audit.Tests;

/// <summary>
/// Module shape and public-surface rules for issue #24 (G2 "NetArchTest and static rules to add"; G1
/// Stories 3, 5 and 6; G3 G4-24-04 and G4-24-05). No application operation reads, updates or deletes an
/// audit record, so the public surface is pinned exactly.
/// </summary>
[Trait("Category", "Architecture")]
public class AuditModuleBoundaryTests
{
    private static readonly Assembly ModuleAssembly = typeof(AuditModule).Assembly;
    private static readonly Assembly ContractsAssembly = typeof(IAuditWriter).Assembly;

    private const BindingFlags AllDeclared =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static IEnumerable<Type> ModuleTypes() =>
        ModuleAssembly.GetTypes().Where(t => t.GetCustomAttribute<CompilerGeneratedAttribute>() is null);

    [Fact]
    public void The_Audit_module_references_no_other_modules_implementation()
    {
        var otherModules = ModuleAssembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(n => n.StartsWith("Decisya.Modules.", StringComparison.Ordinal))
            .Where(n => !n.EndsWith(".Contracts", StringComparison.Ordinal))
            .Where(n => n != ModuleAssembly.GetName().Name);

        otherModules.Should().BeEmpty();
    }

    [Fact]
    public void The_Audit_module_never_reads_the_system_clock_directly()
    {
        var result = Types.InAssembly(ModuleAssembly)
            .Should()
            .NotHaveDependencyOn("NodaTime.SystemClock")
            .GetResult();

        result.IsSuccessful.Should().BeTrue();
    }

    [Fact]
    public void The_Audit_module_references_no_ASP_NET_Core_and_no_Wolverine_assembly_and_declares_no_FrameworkReference()
    {
        var referenced = ModuleAssembly.GetReferencedAssemblies().Select(a => a.Name ?? string.Empty).ToList();

        referenced.Should().NotContain(n => n.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));
        referenced.Should().NotContain(n => n.StartsWith("Wolverine", StringComparison.Ordinal));

        var csproj = File.ReadAllText(RepoPaths.Find(Path.Combine(
            "src", "Modules", "Audit", "Decisya.Modules.Audit", "Decisya.Modules.Audit.csproj")));
        csproj.Should().NotContain("<FrameworkReference");
        csproj.Should().NotContain("<PackageReference Include=\"Wolverine");
        csproj.Should().NotContain("<PackageReference Include=\"Microsoft.AspNetCore");
    }

    [Fact]
    public void The_Audit_module_declares_no_Endpoints_namespace_and_no_Endpoints_folder()
    {
        ModuleTypes()
            .Where(t => (t.Namespace ?? string.Empty).StartsWith("Decisya.Modules.Audit.Endpoints", StringComparison.Ordinal))
            .Should().BeEmpty();

        Directory.Exists(RepoPaths.Find(Path.Combine(
            "src", "Modules", "Audit", "Decisya.Modules.Audit", "Endpoints"))).Should().BeFalse();
    }

    [Fact]
    public void InternalsVisibleTo_is_exactly_the_module_test_assembly()
    {
        ModuleAssembly.GetCustomAttributes<InternalsVisibleToAttribute>()
            .Select(a => a.AssemblyName)
            .Should().Equal("Decisya.Modules.Audit.Tests");
    }

    [Fact]
    public void The_only_AllowCrossTenant_type_in_the_module_is_the_AuditWriter_and_it_is_not_public()
    {
        var attributed = ModuleTypes()
            .Where(t => t.GetCustomAttribute<AllowCrossTenantAttribute>(inherit: false) is not null)
            .ToList();

        var writer = attributed.Should().ContainSingle().Which;
        writer.Name.Should().Be("AuditWriter");
        writer.IsPublic.Should().BeFalse();
        writer.IsVisible.Should().BeFalse();
        writer.GetCustomAttribute<AllowCrossTenantAttribute>()!.Justification.Should().Contain("ADR-0013").And.Contain("ADR-0012");
    }

    // ---- public surface: Story 5 "no application operation reads audit records" ----

    [Fact]
    public void Audit_Contracts_exports_exactly_IAuditWriter_AuditEntry_and_AuditAction()
    {
        ContractsAssembly.GetExportedTypes().Should().BeEquivalentTo([typeof(IAuditWriter), typeof(AuditEntry), typeof(AuditAction)]);
    }

    [Fact]
    public void IAuditWriter_has_exactly_one_method_AppendAsync_of_AuditEntry_DbTransaction_and_CancellationToken()
    {
        var method = typeof(IAuditWriter).GetMethods().Should().ContainSingle().Which;

        method.Name.Should().Be(nameof(IAuditWriter.AppendAsync));
        method.ReturnType.Should().Be<Task>();
        method.GetParameters().Select(p => p.ParameterType).Should().Equal(typeof(AuditEntry), typeof(DbTransaction), typeof(CancellationToken));
        typeof(IAuditWriter).GetProperties().Should().BeEmpty();
        typeof(IAuditWriter).GetEvents().Should().BeEmpty();
    }

    /// <summary>G3 G4-24-04 (T-02): the entry carries no actor, time, trace id or outcome, so a caller can neither forge nor omit them.</summary>
    [Fact]
    public void AuditEntry_is_exactly_TenantId_Action_and_FeatureKey_with_no_actor_time_trace_or_outcome_member()
    {
        typeof(AuditEntry).IsSealed.Should().BeTrue();

        var properties = typeof(AuditEntry).GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        properties.Select(p => (p.Name, p.PropertyType)).Should().BeEquivalentTo(
            new (string, Type)[] { ("TenantId", typeof(TenantId)), ("Action", typeof(AuditAction)), ("FeatureKey", typeof(string)) });

        typeof(AuditEntry).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(f => f.GetCustomAttribute<CompilerGeneratedAttribute>() is null).Should().BeEmpty();

        var constructor = typeof(AuditEntry).GetConstructors().Should().ContainSingle().Which;
        constructor.GetParameters().Select(p => p.Name).Should().Equal("TenantId", "Action", "FeatureKey");

        var memberNames = typeof(AuditEntry).GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.Name).ToList();
        memberNames.Should().NotContain(n =>
            n.Contains("Actor", StringComparison.OrdinalIgnoreCase)
            || n.Contains("User", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Time", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Occurred", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Trace", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Outcome", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Reason", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AuditAction_has_exactly_the_three_values_1_to_3()
    {
        Enum.GetValues<AuditAction>().Select(v => (v.ToString(), (int)v)).Should().BeEquivalentTo(
            new (string, int)[]
            {
                ("EntitlementsTrialStart", 1), ("EntitlementsOverrideGrant", 2), ("EntitlementsOverrideRevoke", 3),
            });
    }

    [Fact]
    public void Decisya_Modules_Audit_exports_exactly_AuditModule_AuditDbContext_and_AuditDbContextOptions()
    {
        // EF Core scaffolds a migration class as public; it carries no operation and no read path, so it is the one allowed extra.
        ModuleAssembly.GetExportedTypes()
            .Where(t => !(t.Namespace == "Decisya.Modules.Audit.Infrastructure.Migrations" && typeof(Microsoft.EntityFrameworkCore.Migrations.Migration).IsAssignableFrom(t)))
            .Should().BeEquivalentTo([typeof(AuditModule), typeof(AuditDbContext), typeof(AuditDbContextOptions)]);
    }

    [Fact]
    public void No_exported_member_signature_mentions_AuditRecord_DbSet_or_IQueryable()
    {
        const BindingFlags exported = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        static bool Mentions(Type type) =>
            type == typeof(AuditRecord)
            || (type.IsGenericType && !type.IsGenericTypeDefinition && (Mentions(type.GetGenericTypeDefinition()) || type.GetGenericArguments().Any(Mentions)))
            || type == typeof(IQueryable) || type == typeof(IQueryable<>) || type == typeof(DbSet<>)
            || (type.HasElementType && Mentions(type.GetElementType()!));

        foreach (var type in ModuleAssembly.GetExportedTypes().Concat(ContractsAssembly.GetExportedTypes()))
        {
            foreach (var member in type.GetMembers(exported))
            {
                // Public and protected members only: an internal member cannot be named outside the module.
                var isExposed = member switch
                {
                    MethodBase m => m.IsPublic || m.IsFamily || m.IsFamilyOrAssembly,
                    PropertyInfo p => (p.GetMethod?.IsPublic ?? false) || (p.GetMethod?.IsFamily ?? false),
                    FieldInfo f => f.IsPublic || f.IsFamily,
                    _ => false,
                };

                if (!isExposed)
                {
                    continue;
                }

                var types = member switch
                {
                    MethodInfo m => m.GetParameters().Select(p => p.ParameterType).Append(m.ReturnType),
                    ConstructorInfo c => c.GetParameters().Select(p => p.ParameterType),
                    PropertyInfo p => [p.PropertyType],
                    FieldInfo f => [f.FieldType],
                    _ => [],
                };

                types.Where(t => Mentions(t)).Should().BeEmpty($"{type.Name}.{member.Name} must not expose AuditRecord, DbSet or IQueryable");
            }
        }
    }

    [Fact]
    public void AuditDbContext_declares_no_public_DbSet_and_its_only_set_is_internal()
    {
        typeof(AuditDbContext).GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Should().BeEmpty();
        typeof(AuditDbContext).GetProperties(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(p => p.PropertyType.IsGenericType && p.PropertyType.GetGenericTypeDefinition() == typeof(DbSet<>))
            .Should().ContainSingle().Which.PropertyType.GetGenericArguments().Single().Should().Be<AuditRecord>();
    }

    [Fact]
    public void AuditRecord_is_internal_with_get_only_properties_and_no_instance_method_besides_the_getters()
    {
        typeof(AuditRecord).IsPublic.Should().BeFalse();
        typeof(AuditRecord).IsVisible.Should().BeFalse();
        typeof(AuditRecord).IsSealed.Should().BeTrue();
        typeof(AuditRecord).GetInterfaces().Should().Contain(typeof(ITenantScoped));

        foreach (var property in typeof(AuditRecord).GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            property.SetMethod.Should().BeNull($"{property.Name} must have no setter, not even a private or init one");
        }

        typeof(AuditRecord).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .Should().BeEmpty("a record has no mutator and no behaviour besides the getters");

        typeof(AuditRecord).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Should().ContainSingle().Which.Name.Should().Be("Create");
    }

    /// <summary>G3 G4-24-05 (T-08): the stored actor id is pseudonymous personal data, masked if it is ever rendered.</summary>
    [Fact]
    public void AuditRecord_ActorUserId_carries_Sensitive()
    {
        typeof(AuditRecord).GetProperty(nameof(AuditRecord.ActorUserId))!
            .GetCustomAttributes(typeof(SensitiveAttribute), inherit: true).Should().NotBeEmpty();
    }

    // ---- Story 3: time is a NodaTime Instant, never a DateTime ----

    [Fact]
    public void No_Domain_or_Application_type_has_a_DateTime_DateTimeOffset_or_TimeZoneInfo_member_or_parameter()
    {
        Type[] banned = [typeof(DateTime), typeof(DateTimeOffset), typeof(DateTime?), typeof(DateTimeOffset?), typeof(TimeZoneInfo)];

        var scoped = ModuleTypes().Where(t =>
            t.Namespace == "Decisya.Modules.Audit.Domain" || t.Namespace == "Decisya.Modules.Audit.Application").ToList();
        scoped.Should().NotBeEmpty();

        foreach (var type in scoped)
        {
            type.GetProperties(AllDeclared).Select(p => p.PropertyType).Should().NotContain(banned, $"{type.Name} properties");
            type.GetFields(AllDeclared).Select(f => f.FieldType).Should().NotContain(banned, $"{type.Name} fields");

            foreach (var method in type.GetMethods(AllDeclared).Cast<MethodBase>().Concat(type.GetConstructors(AllDeclared)))
            {
                method.GetParameters().Select(p => p.ParameterType).Should().NotContain(banned, $"{type.Name}.{method.Name} parameters");
            }
        }
    }

    [Fact]
    public void AuditRecord_OccurredAt_is_an_Instant()
    {
        typeof(AuditRecord).GetProperty(nameof(AuditRecord.OccurredAt))!.PropertyType.Should().Be<Instant>();
    }

    /// <summary>G3 G4-24-04: the writer never writes a placeholder actor, so the module holds no such literal.</summary>
    [Fact]
    public void The_module_source_holds_no_placeholder_actor_literal()
    {
        var root = RepoPaths.Find(Path.Combine("src", "Modules", "Audit", "Decisya.Modules.Audit"));
        var sources = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "Migrations" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .ToList();
        sources.Should().NotBeEmpty();

        var placeholder = new Regex("\"(system|unknown|anonymous)\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        foreach (var source in sources)
        {
            placeholder.IsMatch(File.ReadAllText(source)).Should().BeFalse(source);
        }
    }

    [Fact]
    public void The_AuditWriter_takes_only_the_ambient_tenant_the_caller_and_the_clock_so_it_has_no_logger()
    {
        var writer = ModuleTypes().Single(t => t.Name == "AuditWriter");

        writer.GetConstructors().Single().GetParameters().Select(p => p.ParameterType.Name)
            .Should().Equal("ICurrentTenant", "ICurrentCaller", "IClock");
    }
}
