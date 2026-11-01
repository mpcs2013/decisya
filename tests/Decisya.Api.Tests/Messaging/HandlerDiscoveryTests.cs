using Decisya.Api.Messaging;
using Decisya.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;

namespace Decisya.Api.Tests.Messaging;

public sealed record UnlistedMessage(string Note);

public sealed record AdminLikeMessage(string Note);

/// <summary>A handler Wolverine's conventions would find, that no module lists.</summary>
public static class UnlistedHandler
{
    public static void Handle(UnlistedMessage message) => ArgumentNullException.ThrowIfNull(message);
}

/// <summary>An attributed cross-tenant handler: reachable by an HTTP admin policy, never by a message.</summary>
[AllowCrossTenant("test: a platform-admin operation")]
public static class AdminLikeHandler
{
    public static void Handle(AdminLikeMessage message) => ArgumentNullException.ThrowIfNull(message);
}

/// <summary>
/// Issue #231, G1 Story 3 / G3 T-09, T-10 / architecture rules R4 and G4-231-03 (the part a unit test
/// can show): handler discovery is explicit. Wolverine 6.39.1 ships no runtime compiler, so a listed
/// handler cannot be compiled in these tests; its Static-mode start-up failure names the handlers
/// Wolverine discovered, which is what this class reads (reported at G4).
/// </summary>
[Trait("Category", "Unit")]
public class HandlerDiscoveryTests
{
    // The test assembly is made Wolverine's application assembly, the assembly its conventions scan,
    // so an unlisted conventional handler would be found if scanning were on.
    private static void ScanTheTestAssembly(Microsoft.AspNetCore.Builder.WebApplicationBuilder builder) =>
        builder.Services.ConfigureWolverine(options => options.ApplicationAssembly = typeof(UnlistedHandler).Assembly);

    [Fact]
    public async Task A_conventional_handler_in_the_application_assembly_is_not_discovered_when_no_module_lists_it()
    {
        await using var host = await MessagingHost.StartAsync(configure: ScanTheTestAssembly, probeHandlers: false);

        host.Services.GetRequiredService<Wolverine.Runtime.Handlers.HandlerGraph>().AllChains().Should().BeEmpty();
    }

    [Fact]
    public async Task A_handler_a_module_lists_is_discovered_and_only_that_one()
    {
        var start = async () => await MessagingHost.StartAsync(
            configure: ScanTheTestAssembly, probeHandlers: false, handlerTypes: [typeof(UnlistedHandler)]);

        // Static mode cannot find generated code for it: the failure names what was discovered.
        var failure = (await start.Should().ThrowAsync<Exception>()).Which;
        failure.ToString().Should().Contain(nameof(UnlistedHandler)).And.NotContain(nameof(AdminLikeHandler));
    }

    [Fact]
    public void A_type_that_carries_AllowCrossTenant_is_refused_as_a_message_handler()
    {
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder();

        var act = () => builder.AddDecisyaMessaging([typeof(UnlistedHandler), typeof(AdminLikeHandler)]);

        act.Should().Throw<ArgumentException>().Which.Message.Should().NotContain(nameof(AdminLikeHandler));
    }

    [Fact]
    public void A_null_list_or_a_null_type_is_refused()
    {
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder();

        var nullList = () => builder.AddDecisyaMessaging(null!);
        var nullType = () => builder.AddDecisyaMessaging([[typeof(UnlistedHandler), null!]]);

        nullList.Should().Throw<ArgumentNullException>();
        nullType.Should().Throw<ArgumentNullException>();
    }
}
