using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Decisya.ServiceDefaults.Logging;

/// <summary>
/// Writes exactly one startup <see cref="LogLevel.Warning"/> when the Development host is
/// running on a generated, per-process <c>user_id</c> hash key. The warning names the
/// setting and the consequence; it never contains key material.
/// </summary>
internal sealed class EphemeralUserIdHashKeyWarning(
    UserIdHashKeyProvisioner provisioner,
    IOptions<DecisyaObservabilityOptions> options,
    ILoggerFactory loggerFactory) : IHostedService
{
    internal const string LoggerCategory = "Decisya.ServiceDefaults.Observability";

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Forces the options pipeline, and with it the provisioner, to run.
        _ = options.Value;

        if (provisioner.GeneratedEphemeralKey)
        {
            ObservabilityLog.EphemeralUserIdHashKey(
                loggerFactory.CreateLogger(LoggerCategory),
                DecisyaObservabilityOptions.UserIdHashKeyPath);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
