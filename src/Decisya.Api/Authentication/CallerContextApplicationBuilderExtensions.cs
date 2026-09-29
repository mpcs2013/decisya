namespace Decisya.Api.Authentication;

/// <summary>The single entry point <c>Program</c> calls for caller-context resolution (issue #21, G2).</summary>
public static class CallerContextApplicationBuilderExtensions
{
    /// <summary>
    /// Resolves the current request's caller and tenant from the validated principal's own
    /// claims. Must run after <c>UseAuthentication</c> and before <c>UseRouting</c>, so a
    /// rejection here happens before endpoint selection (G2, G3 G4-21-02).
    /// </summary>
    public static IApplicationBuilder UseCallerContext(this IApplicationBuilder app) =>
        CallerContextMiddleware.Use(app);
}
