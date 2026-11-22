using System.Diagnostics;
using System.Diagnostics.Metrics;
using Decisya.Modules.Ledger.Application;
using Decisya.Modules.Ledger.Endpoints;
using Decisya.Modules.Ledger.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Decisya.Modules.Ledger;

/// <summary>Registers the Ledger module (schema <c>ledger</c>; issue #341, G2).</summary>
public static class LedgerModule
{
    /// <summary>Name of the module's <see cref="ActivitySource"/> and <see cref="Meter"/>.</summary>
    public const string TelemetryName = "Decisya.Ledger";

    /// <summary>The module's Postgres schema.</summary>
    public const string Schema = "ledger";

    /// <summary>The least-privilege database role the API connects as (G2 D1): SELECT, INSERT and UPDATE only.</summary>
    public const string DatabaseRole = "decisya_ledger";

    /// <summary>The name of the policy every write requires: the caller is the owner of the space.</summary>
    public const string OwnerPolicy = "Ledger.Owner";

    internal static readonly ActivitySource ActivitySource = new(TelemetryName);

    internal static readonly Meter Meter = new(TelemetryName);

    /// <summary>
    /// Adds <see cref="LedgerDbContext"/> (plain <c>AddDbContext</c>, never pooled: the #22 constructor convention
    /// forbids pooling), the three write handlers, the read queries, the activity seam and the
    /// <c>Ledger.Owner</c> policy with its handler. All scoped.
    /// </summary>
    /// <exception cref="InvalidOperationException"><paramref name="connectionString"/> is null or blank. The message never includes the connection string itself.</exception>
    public static IServiceCollection AddLedgerModule(this IServiceCollection services, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "Missing required configuration key: ConnectionStrings:ledger.");
        }

        services.AddDbContext<LedgerDbContext>(
            options => LedgerDbContextOptions.Configure(options, connectionString));

        services.AddScoped<CreateAccountHandler>();
        services.AddScoped<RenameAccountHandler>();
        services.AddScoped<ChangeAccountCurrencyHandler>();
        services.AddScoped<AccountQueries>();
        services.AddScoped<IAccountActivity, NoAccountActivity>();

        services.AddAuthorizationBuilder()
            .AddPolicy(OwnerPolicy, policy => policy
                .RequireAuthenticatedUser()
                .RequireClaim("sub")
                .AddRequirements(new LedgerOwnerRequirement()));
        services.AddScoped<IAuthorizationHandler, LedgerOwnerAuthorizationHandler>();

        return services;
    }

    /// <summary>Maps the five <c>/api/ledger</c> operations (accounts: list, read, create, rename, change currency).</summary>
    public static RouteGroupBuilder MapLedgerEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        return LedgerEndpoints.Map(endpoints);
    }
}
