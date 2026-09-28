using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace Decisya.Api.Tests.Authentication;

/// <summary>
/// Story 4 and Story 5 scenario 1 (G2): appends two terminal, test-only branches after the
/// real <c>Decisya.Api</c> pipeline, so both routes still pass through
/// <c>UseExceptionHandler</c>, <c>UseAuthentication</c> and <c>UseAuthorization</c> exactly as
/// any real endpoint would (neither path is a mapped endpoint, so the fallback policy applies
/// to them the same way it would to an unknown path).
/// </summary>
internal sealed class TestOnlyEndpointsStartupFilter(string canary) : IStartupFilter
{
    internal const string ThrowPath = "/__test/throw";
    internal const string ConnectionPath = "/__test/connection";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        next(app);

        app.Map(ThrowPath, branch => branch.Run(_ => throw new InvalidOperationException(canary)));
        app.Map(ConnectionPath, branch => branch.Run(async context =>
        {
            await context.Response.WriteAsync(
                $"{context.Request.Scheme}|{context.Connection.RemoteIpAddress}", context.RequestAborted);
        }));
    };
}
