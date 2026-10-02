using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Decisya.Modules.Entitlements.Contracts;
using Decisya.Modules.Entitlements.Contracts.Admin;
using Decisya.SharedKernel.Tenancy;
using Decisya.TestInfrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;

namespace Decisya.Modules.Admin.Tests.TestSupport;

/// <summary>How the host presents <see cref="IHttpMaxRequestBodySizeFeature"/> to the endpoint (G3 G4-25-04).</summary>
internal enum BodyLimitMode
{
    /// <summary>A writable feature that is enforced while the body is read, as Kestrel does.</summary>
    Writable,

    /// <summary>A read-only feature (the limit cannot be set): the endpoint must fall back to Content-Length.</summary>
    ReadOnly,

    /// <summary>No feature at all (as on the TestServer): the same fallback.</summary>
    Absent,
}

/// <summary>Who the request is, as <c>CallerContextMiddleware</c> would have resolved it.</summary>
internal enum TestCaller
{
    PlatformAdmin,
    NonAdminTenantLess,
    TenantUser,
    TenantUserWithAdminFlag,
    Anonymous,
}

/// <summary>
/// The real <c>Decisya.Modules.Admin</c> endpoints (<c>AddAdminModule</c>, <c>MapAdminEndpoints</c>) on an
/// in-process TestServer, with no database and no Keycloak: authentication is a fixed test scheme, the
/// caller and the tenant resolution come from the <c>X-Test-Caller</c> header, and
/// <see cref="IEntitlementAdminCommands"/> is the recording <see cref="StubCommands"/>. Logging is captured
/// at Trace for every category, and every activity and metric tag is recorded.
/// </summary>
internal sealed class AdminTestHost : IAsyncDisposable
{
    internal const string CallerHeader = "X-Test-Caller";

    private readonly WebApplication _app;

    private AdminTestHost(WebApplication app, StubCommands commands, CapturingLoggerProvider logs)
    {
        _app = app;
        Commands = commands;
        Logs = logs;
        Client = app.GetTestClient();
    }

    public HttpClient Client { get; }

    public StubCommands Commands { get; }

    public CapturingLoggerProvider Logs { get; }

    public EndpointDataSource Endpoints => _app.Services.GetRequiredService<EndpointDataSource>();

    public static async Task<AdminTestHost> StartAsync(BodyLimitMode mode = BodyLimitMode.Writable)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseTestServer();

        var logs = new CapturingLoggerProvider();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Trace);
        builder.Logging.AddFilter(null, LogLevel.Trace);
        builder.Logging.AddProvider(logs);

        var commands = new StubCommands();
        builder.Services.AddSingleton<IEntitlementAdminCommands>(commands);
        builder.Services.AddProblemDetails();
        builder.Services.AddScoped<TestCurrentCaller>();
        builder.Services.AddScoped<ICurrentCaller>(sp => sp.GetRequiredService<TestCurrentCaller>());
        builder.Services.AddScoped<TestCurrentTenant>();
        builder.Services.AddScoped<ICurrentTenant>(sp => sp.GetRequiredService<TestCurrentTenant>());
        builder.Services.AddAuthentication(TestAuthHandler.SchemeName).AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
        builder.Services.AddAdminModule();

        var app = builder.Build();
        app.UseExceptionHandler();
        app.UseMiddleware<BodyLimitEmulation>(mode);
        app.UseAuthentication();
        app.Use(async (context, next) =>
        {
            ApplyCaller(context);
            await next(context);
        });
        app.UseAuthorization();
        app.MapAdminEndpoints();

        await app.StartAsync();
        return new AdminTestHost(app, commands, logs);
    }

    public static string HeaderValue(TestCaller caller) => caller.ToString();

    private static void ApplyCaller(HttpContext context)
    {
        var caller = context.Request.Headers.TryGetValue(CallerHeader, out var header)
            && Enum.TryParse<TestCaller>(header.ToString(), out var parsed)
            ? parsed
            : TestCaller.PlatformAdmin;

        var current = context.RequestServices.GetRequiredService<TestCurrentCaller>();
        var tenant = context.RequestServices.GetRequiredService<TestCurrentTenant>();
        current.Id = TestCurrentCaller.DefaultUserId;
        switch (caller)
        {
            case TestCaller.PlatformAdmin:
                current.IsPlatformAdmin = true;
                tenant.Resolution = TenantResolution.NoTenant;
                break;
            case TestCaller.NonAdminTenantLess:
                current.IsPlatformAdmin = false;
                tenant.Resolution = TenantResolution.NoTenant;
                break;
            case TestCaller.TenantUser:
                current.IsPlatformAdmin = false;
                tenant.Resolution = TenantResolution.For(TenantId.New());
                break;
            case TestCaller.TenantUserWithAdminFlag:
                // Impossible in production (the fact is role AND None); asserts the policy checks both.
                current.IsPlatformAdmin = true;
                tenant.Resolution = TenantResolution.For(TenantId.New());
                break;
            case TestCaller.Anonymous:
            default:
                current.IsPlatformAdmin = false;
                tenant.Resolution = TenantResolution.NoTenant;
                break;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    /// <summary>A fixed identity with a <c>sub</c> claim, unless the caller is <see cref="TestCaller.Anonymous"/>.</summary>
    private sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "Test";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (Request.Headers.TryGetValue(CallerHeader, out var header) && header == nameof(TestCaller.Anonymous))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity([new Claim("sub", TestCurrentCaller.DefaultUserId)], SchemeName);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }

    /// <summary>
    /// Emulates what Kestrel does with <see cref="IHttpMaxRequestBodySizeFeature"/> (the TestServer has none):
    /// in <see cref="BodyLimitMode.Writable"/> the body stream throws <see cref="BadHttpRequestException"/> (413)
    /// once more than the feature's current limit has been read.
    /// </summary>
    private sealed class BodyLimitEmulation(RequestDelegate next, BodyLimitMode mode)
    {
        public Task InvokeAsync(HttpContext context)
        {
            if (mode != BodyLimitMode.Absent)
            {
                var feature = new FakeBodySizeFeature { IsReadOnly = mode == BodyLimitMode.ReadOnly };
                context.Features.Set<IHttpMaxRequestBodySizeFeature>(feature);
                context.Request.Body = new EnforcingStream(context.Request.Body, feature);
            }

            return next(context);
        }
    }

    private sealed class FakeBodySizeFeature : IHttpMaxRequestBodySizeFeature
    {
        public bool IsReadOnly { get; init; }

        public long? MaxRequestBodySize { get; set; } = 30_000_000;
    }

    private sealed class EnforcingStream(Stream inner, FakeBodySizeFeature feature) : Stream
    {
        private long _read;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var count = await inner.ReadAsync(buffer, cancellationToken);
            Account(count);
            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            Account(read);
            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private void Account(int count)
        {
            _read += count;
            if (feature.MaxRequestBodySize is { } limit && _read > limit)
            {
                throw new BadHttpRequestException("Request body too large.", StatusCodes.Status413PayloadTooLarge);
            }
        }
    }
}

/// <summary>One call the endpoints made on <see cref="IEntitlementAdminCommands"/>.</summary>
internal sealed record StubCall(string Action, TenantId Tenant, FeatureKey? Feature, string? Reason, Instant? ExpiresAt);

/// <summary>A recording <see cref="IEntitlementAdminCommands"/>: returns <see cref="Result"/> or throws <see cref="Fault"/>.</summary>
internal sealed class StubCommands : IEntitlementAdminCommands
{
    private readonly ConcurrentQueue<StubCall> _calls = new();

    public EntitlementAdminResult Result { get; set; } = EntitlementAdminResult.Succeeded;

    public Exception? Fault { get; set; }

    public IReadOnlyList<StubCall> Calls => [.. _calls];

    public Task<EntitlementAdminResult> StartTrialAsync(TenantId tenantId, CancellationToken cancellationToken = default) =>
        Record(new StubCall("start_trial", tenantId, null, null, null));

    public Task<EntitlementAdminResult> GrantOverrideAsync(
        TenantId tenantId, FeatureKey feature, string reason, Instant? expiresAt, CancellationToken cancellationToken = default) =>
        Record(new StubCall("grant_override", tenantId, feature, reason, expiresAt));

    public Task<EntitlementAdminResult> RevokeOverrideAsync(TenantId tenantId, FeatureKey feature, CancellationToken cancellationToken = default) =>
        Record(new StubCall("revoke_override", tenantId, feature, null, null));

    private Task<EntitlementAdminResult> Record(StubCall call)
    {
        _calls.Enqueue(call);
        return Fault is { } fault ? throw fault : Task.FromResult(Result);
    }
}

/// <summary>An in-memory logger provider that captures every record at every level (mirrors the Entitlements one).</summary>
internal sealed record CapturedLogRecord(LogLevel Level, string Category, string Message, string StateText, string? ExceptionText)
{
    public bool Contains(string value) =>
        Message.Contains(value, StringComparison.Ordinal)
        || StateText.Contains(value, StringComparison.Ordinal)
        || (ExceptionText?.Contains(value, StringComparison.Ordinal) ?? false);
}

internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<CapturedLogRecord> Records { get; } = new();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(CapturingLoggerProvider provider, string categoryName) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            var message = formatter(state, exception);
            var stateText = state is IEnumerable<KeyValuePair<string, object>> structuredState
                ? string.Join("; ", structuredState.Select(pair => $"{pair.Key}={pair.Value}"))
                : state?.ToString() ?? string.Empty;

            provider.Records.Enqueue(new CapturedLogRecord(logLevel, categoryName, message, stateText, exception?.ToString()));
        }
    }
}
