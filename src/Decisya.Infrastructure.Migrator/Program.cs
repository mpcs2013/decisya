using Decisya.Infrastructure.Migrator;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);
builder.AddServiceDefaults();

using var host = builder.Build();

var logger = host.Services.GetRequiredService<ILogger<Program>>();
var configuration = host.Services.GetRequiredService<IConfiguration>();

try
{
    await MigrationRunner.RunAsync(
        configuration.GetConnectionString("decisya"),
        configuration["Migrator:TenancyRolePassword"],
        configuration["Migrator:EntitlementsRolePassword"],
        CancellationToken.None);
}
catch (InvalidOperationException ex)
{
    // The message names only the missing or invalid configuration key, never its value
    // (G3 G4-21-05): safe to log as-is.
    MigratorLog.ConfigurationError(logger, ex.Message);
    return 1;
}

MigratorLog.MigrationCompleted(logger);
return 0;
