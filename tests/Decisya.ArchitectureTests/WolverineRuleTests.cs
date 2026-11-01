using System.Reflection;
using Decisya.ArchitectureTests.Fixtures.TenancyCompliant.ChainlessRoutes;
using Decisya.ArchitectureTests.Fixtures.TenancyViolations.EnvelopeStorage;
using Decisya.ArchitectureTests.Fixtures.TenancyViolations.UnstampedBus;
using Decisya.ArchitectureTests.Fixtures.TenancyViolations.WolverineTenantSetters;
using Decisya.Infrastructure.Persistence;
using Decisya.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Decisya.ArchitectureTests;

/// <summary>
/// Issue #231, architecture rules R6 (<see cref="WolverineTenantSetterRule"/>), R7
/// (<see cref="EnvelopeStorageMappingRule"/> and <see cref="TenantModelRule"/>) and R14
/// (<see cref="UnstampedBusRule"/>): each is red on a fixture per target (<c>Fixtures/TenancyViolations</c>),
/// green on a compliant fixture (<c>Fixtures/TenancyCompliant</c>), exact in its exemptions (G3 S-09), and
/// green on today's <c>src</c> (<see cref="WolverineScope"/>, <see cref="KeyScope"/>). R1 to R3 are proved
/// by <c>Decisya.Api.Tests</c>.
/// </summary>
[Trait("Category", "Architecture")]
public class WolverineRuleTests
{
    private const string ModelOnlyConnectionString =
        "Host=model-only.invalid;Database=architecture-tests;Username=architecture-tests;Password=architecture-tests";

    private static readonly string ViolationsAssemblyName = typeof(SetsEnvelopeTenant).Assembly.GetName().Name!;

    // ---- R6: every Wolverine member whose name contains "Tenant" is referenced only by the stamp ----

    [Theory]
    [InlineData(typeof(SetsEnvelopeTenant), "set_TenantId")]
    [InlineData(typeof(SetsDeliveryOptionsTenant), "set_TenantId")]
    [InlineData(typeof(InvokesForAnotherTenant), "InvokeForTenantAsync")]
    [InlineData(typeof(SetsBusTenant), "set_TenantId")]
    [InlineData(typeof(ReadsEnvelopeTenantOutsideTheReceiveMiddleware), "get_TenantId")]
    public void R6_fails_on_a_Wolverine_tenant_member_outside_the_stamp_and_names_the_type_and_the_member(Type fixture, string member)
    {
        var failures = WolverineTenantSetterRule.Evaluate([fixture.Assembly]);

        failures.Should().Contain(f =>
            f.StartsWith(fixture.FullName!, StringComparison.Ordinal) && f.EndsWith("." + member, StringComparison.Ordinal));
    }

    [Fact]
    public void R6_passes_a_type_that_only_takes_and_calls_IMessageBus()
    {
        var failures = WolverineTenantSetterRule.Evaluate([typeof(BusOnlyUser).Assembly]);

        failures.Should().BeEmpty(string.Join("; ", failures));
    }

    [Fact]
    public void R6_is_green_on_today_src_and_both_exemptions_name_existing_types()
    {
        var failures = WolverineTenantSetterRule.Evaluate(WolverineScope.Assemblies);

        failures.Should().BeEmpty(string.Join("; ", failures));
        WolverineTenantSetterRule.UnknownExemptions(WolverineScope.Assemblies).Should().BeEmpty();
        WolverineTenantSetterRule.Writers.Should().Equal(WolverineTenantSetterRule.StampType);
    }

    [Fact]
    public void R6_exemption_is_an_exact_type_in_the_exact_assembly_never_a_namespace_or_a_same_named_type_elsewhere()
    {
        var fixture = typeof(SetsDeliveryOptionsTenant);
        var assemblies = new[] { fixture.Assembly };
        var none = new HashSet<string>(StringComparer.Ordinal);
        var exactName = new HashSet<string>(StringComparer.Ordinal) { fixture.FullName! };

        // Exact name in the exact assembly: exempt. The other fixtures stay red.
        var exempt = WolverineTenantSetterRule.Evaluate(assemblies, exactName, none, ViolationsAssemblyName);
        exempt.Should().NotContain(f => f.StartsWith(fixture.FullName!, StringComparison.Ordinal));
        exempt.Should().Contain(f => f.StartsWith(typeof(SetsEnvelopeTenant).FullName!, StringComparison.Ordinal));

        // The same exact name, but the exemption is for another assembly: still red.
        WolverineTenantSetterRule.Evaluate(assemblies, exactName, none, "Decisya.Api")
            .Should().Contain(f => f.StartsWith(fixture.FullName!, StringComparison.Ordinal));

        // A namespace, a prefix or a suffix of the name: still red.
        foreach (var notExact in new[] { fixture.Namespace!, fixture.Namespace + ".*", fixture.FullName + "X", fixture.Name })
        {
            var names = new HashSet<string>(StringComparer.Ordinal) { notExact };
            WolverineTenantSetterRule.Evaluate(assemblies, names, none, ViolationsAssemblyName)
                .Should().Contain(f => f.StartsWith(fixture.FullName!, StringComparison.Ordinal), $"'{notExact}' is not the exact type name");
        }
    }

    [Fact]
    public void R6_getter_exemption_covers_get_TenantId_only_and_never_a_setter()
    {
        var reader = typeof(ReadsEnvelopeTenantOutsideTheReceiveMiddleware);
        var setter = typeof(SetsEnvelopeTenant);
        var none = new HashSet<string>(StringComparer.Ordinal);
        var readers = new HashSet<string>(StringComparer.Ordinal) { reader.FullName!, setter.FullName! };

        var failures = WolverineTenantSetterRule.Evaluate([reader.Assembly], none, readers, ViolationsAssemblyName);

        failures.Should().NotContain(f => f.StartsWith(reader.FullName!, StringComparison.Ordinal) && f.EndsWith(".get_TenantId", StringComparison.Ordinal));
        failures.Should().Contain(
            f => f.StartsWith(setter.FullName!, StringComparison.Ordinal) && f.EndsWith(".set_TenantId", StringComparison.Ordinal),
            "a getter exemption never covers set_TenantId");
    }

    // ---- R7: no Wolverine envelope entity in any EF model (G1 Story 4 scenarios 2 to 4) ----

    [Fact]
    public void R7_fails_on_a_call_to_MapWolverineEnvelopeStorage_and_names_the_type()
    {
        var failures = EnvelopeStorageMappingRule.Evaluate([typeof(MapsEnvelopeStorage).Assembly]);

        failures.Should().Contain(f =>
            f.StartsWith(typeof(MapsEnvelopeStorage).FullName!, StringComparison.Ordinal)
            && f.EndsWith("." + EnvelopeStorageMappingRule.BannedMethod, StringComparison.Ordinal));
    }

    [Fact]
    public void R7_is_green_on_today_src()
    {
        var failures = EnvelopeStorageMappingRule.Evaluate(WolverineScope.Assemblies);

        failures.Should().BeEmpty(string.Join("; ", failures));
    }

    [Fact]
    public void R7_TenantModelRule_fails_on_a_context_that_maps_Wolverine_Envelope_and_names_the_context()
    {
        var result = TenantModelRule.Evaluate(typeof(EnvelopeMappingDbContext).Assembly);

        result.IsSuccessful.Should().BeFalse();
        result.FailingTypeNames.Should().Contain(t => t.Contains(nameof(EnvelopeMappingDbContext), StringComparison.Ordinal));
    }

    [Fact]
    public void R7_building_the_model_of_that_context_throws_a_TenantIsolationException_that_names_the_entity_type()
    {
        var options = new DbContextOptionsBuilder<EnvelopeMappingDbContext>().UseNpgsql(ModelOnlyConnectionString).Options;
        using var context = new EnvelopeMappingDbContext(options, new InvalidTenant());

        var act = () => context.Model;

        var thrown = act.Should().Throw<TenantIsolationException>().Which;
        thrown.Violation.Should().Be(TenantIsolationViolation.UnscopedEntityType);
        thrown.EntityTypeNames.Should().Contain("Wolverine.Envelope");
    }

    [Fact]
    public void R7_the_tenant_model_rule_stays_exception_free_it_has_no_allow_list_parameter()
    {
        var evaluate = typeof(TenantModelRule)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => string.Equals(m.Name, nameof(TenantModelRule.Evaluate), StringComparison.Ordinal))
            .ToList();

        evaluate.Should().ContainSingle();
        // Assemblies are its only input: no type name, namespace or pattern can be exempted.
        evaluate[0].GetParameters().Select(p => p.ParameterType).Should().Equal(typeof(Assembly[]));
    }

    [Fact]
    public void R7_the_tenant_model_rule_passes_over_the_scope_with_the_Wolverine_hosts_included()
    {
        var result = TenantModelRule.Evaluate(WolverineScope.Assemblies);

        result.IsSuccessful.Should().BeTrue(string.Join(", ", result.FailingTypeNames ?? []));
    }

    // ---- R14: no type references an unstamped bus ----

    [Theory]
    [InlineData(typeof(TakesMessageContext), "Wolverine.IMessageContext")]
    [InlineData(typeof(HoldsMessageContextField), "Wolverine.Runtime.MessageContext")]
    [InlineData(typeof(TakesMessageBus), "Wolverine.Runtime.MessageBus")]
    [InlineData(typeof(CallsHostMessageBus), UnstampedBusRule.HostCallTarget)]
    public void R14_fails_on_each_unstamped_bus_and_names_the_type_and_the_target(Type fixture, string target)
    {
        var failures = UnstampedBusRule.Evaluate([fixture.Assembly]);

        failures.Should().Contain(f =>
            f.StartsWith(fixture.FullName!, StringComparison.Ordinal) && f.EndsWith(target, StringComparison.Ordinal));
    }

    [Fact]
    public void R14_fails_on_a_type_in_the_namespace_Decisya_Api_Messaging_declared_in_another_assembly()
    {
        var misnamed = typeof(Decisya.Api.Messaging.MisnamedBusUser);
        misnamed.Namespace.Should().Be("Decisya.Api.Messaging");

        var failures = UnstampedBusRule.Evaluate([misnamed.Assembly]);

        failures.Should().Contain(f =>
            f.StartsWith(misnamed.FullName!, StringComparison.Ordinal) && f.EndsWith("Wolverine.IMessageContext", StringComparison.Ordinal));
    }

    [Fact]
    public void R14_passes_a_type_that_only_takes_and_calls_IMessageBus()
    {
        var failures = UnstampedBusRule.Evaluate([typeof(BusOnlyUser).Assembly]);

        failures.Should().BeEmpty(string.Join("; ", failures));
    }

    [Fact]
    public void R14_is_green_on_today_src_and_its_exemption_list_is_empty()
    {
        var failures = UnstampedBusRule.Evaluate(KeyScope.Assemblies);

        failures.Should().BeEmpty(string.Join("; ", failures));
        UnstampedBusRule.Exemptions.Should().BeEmpty("S-09: exact type names, none in #231, not the namespace Decisya.Api.Messaging");
        UnstampedBusRule.UnknownExemptions(KeyScope.Assemblies).Should().BeEmpty();
    }

    [Fact]
    public void R14_exemption_is_an_exact_type_in_the_exact_assembly_never_the_namespace()
    {
        var misnamed = typeof(Decisya.Api.Messaging.MisnamedBusUser);
        var assemblies = new[] { misnamed.Assembly };
        var exactName = new HashSet<string>(StringComparer.Ordinal) { misnamed.FullName! };

        // Exact name in the exact assembly: exempt.
        UnstampedBusRule.Evaluate(assemblies, exactName, ViolationsAssemblyName)
            .Should().NotContain(f => f.StartsWith(misnamed.FullName!, StringComparison.Ordinal));

        // The same name, but the exemption is for Decisya.Api and this is another assembly: still red.
        UnstampedBusRule.Evaluate(assemblies, exactName, UnstampedBusRule.ExemptAssembly)
            .Should().Contain(f => f.StartsWith(misnamed.FullName!, StringComparison.Ordinal));

        // The namespace G2 first proposed, or a prefix of the name: still red.
        foreach (var notExact in new[] { "Decisya.Api.Messaging", "Decisya.Api.Messaging.*", misnamed.FullName + "X" })
        {
            var names = new HashSet<string>(StringComparer.Ordinal) { notExact };
            UnstampedBusRule.Evaluate(assemblies, names, ViolationsAssemblyName)
                .Should().Contain(f => f.StartsWith(misnamed.FullName!, StringComparison.Ordinal), $"'{notExact}' is not the exact type name");
        }
    }

    private sealed class InvalidTenant : ICurrentTenant
    {
        public TenantResolution Resolution => TenantResolution.Invalid;
    }
}
