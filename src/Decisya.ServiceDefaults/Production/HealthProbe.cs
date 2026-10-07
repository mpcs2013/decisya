using System.Net;
using Decisya.ServiceDefaults.Production;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// The <c>--health-probe</c> command line mode (issue #120, D6). The chiseled images have no
/// shell and no curl, so the Compose healthcheck runs the service's own binary with this
/// argument. It takes no URL and no port: it asks <c>http://localhost:8081/health</c> and
/// nothing else, so it cannot be turned into a request to another address.
/// </summary>
/// <remarks>
/// Exit codes: <c>0</c> the service answered <c>200</c>; <c>1</c> anything else (503, another
/// status, no answer, timeout). The probe writes nothing to stdout or stderr, so a probe
/// never adds a log line.
/// </remarks>
public static class HealthProbe
{
    /// <summary>The command line argument that selects probe mode.</summary>
    public const string Argument = "--health-probe";

    /// <summary>Exit code: healthy.</summary>
    public const int ExitHealthy = 0;

    /// <summary>Exit code: unhealthy, unreachable or timed out.</summary>
    public const int ExitUnhealthy = 1;

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);

    /// <summary>True when <paramref name="args"/> contains exactly the probe argument.</summary>
    public static bool IsRequested(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.Contains(Argument, StringComparer.Ordinal);
    }

    /// <summary>
    /// Call first in <c>Program.cs</c>, before the host is built. When the probe argument is
    /// present it runs the probe and ends the process with the exit code. Otherwise it
    /// returns and the program continues.
    /// </summary>
    public static void ExitIfRequested(string[] args)
    {
        if (IsRequested(args))
        {
            Environment.Exit(RunAsync(ManagementPort.Default, DefaultTimeout, CancellationToken.None)
                .GetAwaiter().GetResult());
        }
    }

    internal static async Task<int> RunAsync(int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            using var handler = new SocketsHttpHandler
            {
                UseProxy = false,
                AllowAutoRedirect = false,
                ConnectTimeout = timeout,
            };
            using var client = new HttpClient(handler) { Timeout = timeout };
            using var response = await client.GetAsync(
                new Uri($"http://localhost:{port}/health"), HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            return response.StatusCode == HttpStatusCode.OK ? ExitHealthy : ExitUnhealthy;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return ExitUnhealthy;
        }
    }
}
