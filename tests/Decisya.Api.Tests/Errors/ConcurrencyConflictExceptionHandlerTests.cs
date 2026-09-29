using System.Text.Json;
using Decisya.Api.Errors;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Decisya.Api.Tests.Errors;

/// <summary>
/// Issue #21, Story 5 (carries in #22 B-3); G2, G4-21-04; NFR-33: a
/// <see cref="DbUpdateConcurrencyException"/> — direct, or wrapped as an
/// <see cref="Exception.InnerException"/> — is reported to the caller as a generic 404, never
/// with an entity name, column name or row value from either side of the conflict.
/// </summary>
public class ConcurrencyConflictExceptionHandlerTests
{
    [Fact]
    public async Task A_direct_DbUpdateConcurrencyException_is_reported_as_a_generic_404()
    {
        var result = await InvokeAsync(new DbUpdateConcurrencyException("conflict"));

        AssertGeneric404Body(result);
    }

    [Fact]
    public async Task A_wrapped_DbUpdateConcurrencyException_is_reported_as_a_generic_404()
    {
        var inner = new DbUpdateConcurrencyException("conflict");
        var wrapper = new InvalidOperationException("outer", inner);

        var result = await InvokeAsync(wrapper);

        AssertGeneric404Body(result);
    }

    [Fact]
    public async Task An_unrelated_exception_is_not_handled()
    {
        var result = await InvokeAsync(new InvalidOperationException("not a concurrency conflict"));

        result.Handled.Should().BeFalse();
    }

    private static async Task<(bool Handled, int StatusCode, string? CacheControl, JsonElement Body)> InvokeAsync(Exception exception)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddProblemDetails();
        await using var provider = services.BuildServiceProvider();

        using var responseBody = new MemoryStream();
        var httpContext = new DefaultHttpContext
        {
            RequestServices = provider,
            TraceIdentifier = "trace-1234-5678",
        };
        httpContext.Response.Body = responseBody;

        var handler = new ConcurrencyConflictExceptionHandler(
            provider.GetRequiredService<IProblemDetailsService>(),
            NullLogger<ConcurrencyConflictExceptionHandler>.Instance);

        var handled = await handler.TryHandleAsync(httpContext, exception, TestContext.Current.CancellationToken);

        responseBody.Position = 0;
        using var document = responseBody.Length == 0
            ? null
            : await JsonDocument.ParseAsync(responseBody, cancellationToken: TestContext.Current.CancellationToken);

        return (
            handled,
            httpContext.Response.StatusCode,
            httpContext.Response.Headers.CacheControl.ToString(),
            document is null ? default : document.RootElement.Clone());
    }

    private static void AssertGeneric404Body((bool Handled, int StatusCode, string? CacheControl, JsonElement Body) result)
    {
        result.Handled.Should().BeTrue();
        result.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        result.CacheControl.Should().Be("no-store");

        var propertyNames = result.Body.EnumerateObject().Select(p => p.Name).ToArray();
        propertyNames.Should().BeEquivalentTo(["type", "title", "status", "traceId"],
            "the body must carry no entity name, column name or row value from either side of the conflict");

        result.Body.GetProperty("status").GetInt32().Should().Be(404);
        result.Body.GetProperty("traceId").GetString().Should().Be("trace-1234-5678");
    }
}
