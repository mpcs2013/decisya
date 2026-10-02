using Decisya.Api.Authentication;
using Decisya.Modules.Tenancy;
using Decisya.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Decisya.Api.Tests.Authentication;

/// <summary>
/// G3 G4-21-01 (closes #22 B-1; T-01, T-02): exactly one scoped registration for
/// <see cref="ICurrentTenant"/> and <see cref="ICurrentCaller"/>, DI scope validation in every
/// environment (not just Development), and <see cref="RequestCaller.Set"/>'s own fail-closed
/// shape before and after it runs.
/// </summary>
/// <remarks>
/// Builds the same production entry points (<c>AddApiAuthentication</c>,
/// <c>AddTenancyModule</c>) <c>Program.cs</c> calls, rather than going through
/// <c>WebApplicationFactory&lt;Program&gt;</c>: <see cref="ApiJwtOptionsTests"/> already found
/// that a full host build failure races against other, concurrently-starting test classes'
/// hosts under <c>HostFactoryResolver</c>, occasionally surfacing a different host's teardown
/// exception instead of this class's own. Building and validating the service collection
/// directly removes the host — and that race — entirely, while still exercising the real
/// registration code every production host runs.
/// </remarks>
public class CallerContextTests
{
    private const string PlaceholderTenancyConnectionString =
        "Host=db.invalid;Port=5432;Database=decisya;Username=decisya_tenancy;Password=placeholder";

    [Fact]
    public void ICurrentTenant_and_ICurrentCaller_are_registered_exactly_once_as_Scoped()
    {
        var services = CreateBuilder(Environments.Production).Services;

        AssertExactlyOneScopedDescriptor<ICurrentTenant>(services);
        AssertExactlyOneScopedDescriptor<ICurrentCaller>(services);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public void A_singleton_capturing_ICurrentTenant_fails_to_build_in_every_environment(string environmentName)
    {
        var builder = CreateBuilder(environmentName);
        builder.Services.AddSingleton<SingletonCapturingCurrentTenant>();
        builder.Host.UseDefaultServiceProvider(options =>
        {
            options.ValidateScopes = true;
            options.ValidateOnBuild = true;
        });

        var exception = Record.Exception(() => builder.Build());

        exception.Should().NotBeNull(
            $"a singleton capturing ICurrentTenant must fail DI validation in {environmentName}, not only in Production");
    }

    [Fact]
    public void A_Production_host_without_the_explicit_scope_validation_call_would_accept_the_same_singleton()
    {
        // Control case: the generic host's own default (ValidateScopes/ValidateOnBuild only in
        // Development) is exactly the gap #21 closes — proves the parameterised test's failure
        // above comes from Program.cs's explicit UseDefaultServiceProvider call, not from some
        // other registration problem, by showing Production alone (with no such call) still
        // builds successfully with the very same captive dependency.
        var builder = CreateBuilder(Environments.Production);
        builder.Services.AddSingleton<SingletonCapturingCurrentTenant>();

        using var app = builder.Build();

        app.Should().NotBeNull();
    }

    /// <summary>Story 4 scenario 2: within one scope, every read of the current tenant, through either interface, is the same resolution.</summary>
    [Fact]
    public void Every_read_of_the_current_tenant_within_one_request_scope_returns_the_identical_resolution()
    {
        using var app = CreateBuilder(Environments.Production).Build();
        using var scope = app.Services.CreateScope();
        var tenant = scope.ServiceProvider.GetRequiredService<ICurrentTenant>();
        var caller = scope.ServiceProvider.GetRequiredService<ICurrentCaller>();
        var expected = TenantResolution.For(TenantId.New());
        ((RequestCaller)caller).Set(expected, "dev-alice", isPlatformAdmin: false);

        tenant.Should().BeSameAs(caller, "one scoped instance serves both interfaces");
        tenant.Resolution.Should().Be(expected);
        scope.ServiceProvider.GetRequiredService<ICurrentTenant>().Resolution.Should().Be(expected);
    }

    [Fact]
    public void Set_called_twice_throws_even_with_identical_values()
    {
        var caller = new RequestCaller();
        caller.Set(TenantResolution.NoTenant, "dev-alice", isPlatformAdmin: false);

        var exception = Record.Exception(() => caller.Set(TenantResolution.NoTenant, "dev-alice", isPlatformAdmin: false));

        exception.Should().BeOfType<InvalidOperationException>();
    }

    [Fact]
    public void Before_Set_the_resolution_is_Invalid_and_UserId_throws()
    {
        var caller = new RequestCaller();

        caller.Resolution.Kind.Should().Be(TenantResolutionKind.Invalid);
        var exception = Record.Exception(() => caller.UserId);
        exception.Should().BeOfType<InvalidOperationException>();
    }

    /// <summary>G3 G4-25-01 (T-03): the admin fact is false before <c>Set</c> and does not throw.</summary>
    [Fact]
    public void Before_Set_IsPlatformAdmin_is_false_and_does_not_throw()
    {
        ICurrentCaller caller = new RequestCaller();

        caller.IsPlatformAdmin.Should().BeFalse();
    }

    [Fact]
    public void Set_assigns_IsPlatformAdmin_for_a_tenant_less_caller()
    {
        var caller = new RequestCaller();

        caller.Set(TenantResolution.NoTenant, "dev-admin", isPlatformAdmin: true);

        caller.IsPlatformAdmin.Should().BeTrue();
    }

    /// <summary>The setter itself enforces "role and no tenant" (T-03), not only its one caller.</summary>
    [Fact]
    public void Set_never_makes_a_tenant_caller_a_platform_admin()
    {
        var caller = new RequestCaller();

        caller.Set(TenantResolution.For(TenantId.New()), "dev-admin", isPlatformAdmin: true);

        caller.IsPlatformAdmin.Should().BeFalse();
    }

    [Fact]
    public void A_second_Set_cannot_flip_IsPlatformAdmin()
    {
        var caller = new RequestCaller();
        caller.Set(TenantResolution.NoTenant, "dev-alice", isPlatformAdmin: false);

        Record.Exception(() => caller.Set(TenantResolution.NoTenant, "dev-alice", isPlatformAdmin: true))
            .Should().BeOfType<InvalidOperationException>();
        caller.IsPlatformAdmin.Should().BeFalse();
    }

    [Fact]
    public void IsPlatformAdmin_has_no_public_or_internal_setter()
    {
        var property = typeof(RequestCaller).GetProperty(nameof(RequestCaller.IsPlatformAdmin))!;
        var setter = property.GetSetMethod(nonPublic: true);

        setter.Should().NotBeNull();
        setter!.IsPrivate.Should().BeTrue("only RequestCaller.Set assigns the fact");
    }

    private static WebApplicationBuilder CreateBuilder(string environmentName)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environmentName });
        builder.Configuration.AddInMemoryCollection(
        [
            new("Api:Jwt:Authority", "https://canary.test/realms/decisya"),
            new("ConnectionStrings:tenancy", PlaceholderTenancyConnectionString),
        ]);

        builder.AddApiAuthentication();
        builder.Services.AddTenancyModule(builder.Configuration.GetConnectionString("tenancy")!);

        return builder;
    }

    private static void AssertExactlyOneScopedDescriptor<TService>(IServiceCollection services)
    {
        var descriptors = services.Where(d => d.ServiceType == typeof(TService)).ToList();

        descriptors.Should().HaveCount(1, $"exactly one {typeof(TService).Name} registration should exist");
        descriptors[0].Lifetime.Should().Be(ServiceLifetime.Scoped);
    }

    private sealed class SingletonCapturingCurrentTenant(ICurrentTenant tenant)
    {
        internal ICurrentTenant Tenant { get; } = tenant;
    }
}
