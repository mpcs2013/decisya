using System.Text.Json;
using Decisya.Api.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Decisya.Api.Tests.Authentication;

/// <summary>
/// G3 G4-25-03 (T-07), the handler half: only <see cref="PolicyAuthorizationResult.Forbidden"/>
/// is rewritten, to the generic ProblemDetails; everything else is the framework's; nothing is
/// written once the response has started; no failure reason, policy name or claim leaks.
/// </summary>
public class ProblemDetailsAuthorizationResultHandlerTests
{
    private const string CanaryFailureReason = "CANARY-FAILURE-REASON-9f3a";

    private static readonly AuthorizationPolicy Policy = new AuthorizationPolicyBuilder()
        .RequireAssertion(_ => false)
        .Build();

    [Fact]
    public async Task Forbidden_writes_a_generic_403_with_exactly_type_title_status_and_traceId()
    {
        var (context, body) = CreateContext();
        var nextCalled = false;

        await new ProblemDetailsAuthorizationResultHandler().HandleAsync(
            _ => { nextCalled = true; return Task.CompletedTask; }, context, Policy, PolicyAuthorizationResult.Forbid());

        nextCalled.Should().BeFalse();
        context.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        context.Response.Headers.WWWAuthenticate.Should().BeEmpty();
        using var document = JsonDocument.Parse(body.ToArray());
        document.RootElement.EnumerateObject().Select(p => p.Name)
            .Should().BeEquivalentTo(["type", "title", "status", "traceId"]);
        document.RootElement.GetProperty("status").GetInt32().Should().Be(403);
    }

    [Fact]
    public async Task Forbidden_never_echoes_the_failure_reasons()
    {
        var (context, body) = CreateContext();
        var failure = AuthorizationFailure.Failed([new AuthorizationFailureReason(new NoopHandler(), CanaryFailureReason)]);

        await new ProblemDetailsAuthorizationResultHandler().HandleAsync(
            _ => Task.CompletedTask, context, Policy, PolicyAuthorizationResult.Forbid(failure));

        var text = System.Text.Encoding.UTF8.GetString(body.ToArray());
        text.Should().NotContain(CanaryFailureReason);
        text.Should().NotContain("RequireAssertion");
        text.Should().NotContain(nameof(NoopHandler));
    }

    [Fact]
    public async Task Forbidden_writes_nothing_once_the_response_has_started()
    {
        var (context, body) = CreateContext(started: true);
        var nextCalled = false;

        var exception = await Record.ExceptionAsync(() => new ProblemDetailsAuthorizationResultHandler().HandleAsync(
            _ => { nextCalled = true; return Task.CompletedTask; }, context, Policy, PolicyAuthorizationResult.Forbid()));

        exception.Should().BeNull();
        nextCalled.Should().BeFalse();
        body.Length.Should().Be(0);
    }

    [Fact]
    public async Task A_success_is_delegated_to_the_framework_handler_which_runs_next()
    {
        var (context, body) = CreateContext();
        var nextCalled = false;

        await new ProblemDetailsAuthorizationResultHandler().HandleAsync(
            _ => { nextCalled = true; return Task.CompletedTask; }, context, Policy, PolicyAuthorizationResult.Success());

        nextCalled.Should().BeTrue();
        body.Length.Should().Be(0);
    }

    private static (DefaultHttpContext Context, MemoryStream Body) CreateContext(bool started = false)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddProblemDetails();
        services.AddAuthentication();

        var body = new MemoryStream();
        var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        context.Response.Body = body;
        if (started)
        {
            context.Features.Set<IHttpResponseFeature>(new StartedResponseFeature { Body = body });
        }

        return (context, body);
    }

    private sealed class StartedResponseFeature : HttpResponseFeature
    {
        public override bool HasStarted => true;
    }

    private sealed class NoopHandler : IAuthorizationHandler
    {
        public Task HandleAsync(AuthorizationHandlerContext context) => Task.CompletedTask;
    }
}
