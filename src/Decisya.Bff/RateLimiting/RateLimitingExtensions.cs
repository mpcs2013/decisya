using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Decisya.Bff.RateLimiting;

/// <summary>
/// The only two calls <c>Program.cs</c> makes into the limiter (#122, G2 "Boundaries"). Everything that
/// depends on <c>System.Threading.RateLimiting</c> or <c>Microsoft.AspNetCore.RateLimiting</c> stays in
/// this namespace (NetArchTest rule 1).
/// </summary>
internal static class RateLimitingExtensions
{
    internal static IServiceCollection AddBffRateLimiting(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // In every environment: a bad or unknown limit setting stops the start (G1 Story 6).
        services.AddOptions<BffRateLimitOptions>()
            .Configure<IConfiguration>(static (options, configuration) =>
                options.Load(configuration.GetSection(BffRateLimitOptions.SectionName)))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<BffRateLimitOptions>, BffRateLimitOptionsValidator>();

        services.AddSingleton<RateLimitPartitionStats>();
        services.AddSingleton<TrustedProxies>();
        services.AddSingleton<SessionPartitionHasher>();
        services.AddSingleton<BffRateLimiterHolder>();

        // The limits are read when the limiter is first built, not here, so the host's final
        // configuration (including values added after Program's top-level code) applies.
        services.AddRateLimiter(static _ => { });
        services.AddOptions<RateLimiterOptions>()
            .Configure<BffRateLimiterHolder>(static (options, holder) =>
            {
                options.GlobalLimiter = holder.Limiter;
                options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                options.OnRejected = RateLimitResponses.WriteAsync;
            });

        return services;
    }

    /// <summary>
    /// Pipeline steps 5 and 6: the partition step, then the rate limiter. Call it after forwarded headers,
    /// security headers, the exception handler and the static assets, and before the session-fixation
    /// guard, authentication, authorization and the proxy.
    /// </summary>
    internal static IApplicationBuilder UseBffRateLimiting(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.UseMiddleware<RateLimitPartitionMiddleware>();
        app.UseRateLimiter();
        return app;
    }
}
