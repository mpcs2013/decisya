// #20 (0.08): JWT bearer validation, the fallback authorization policy, generic exception
// handling in every environment, and the first endpoint (GET /api/whoami). Program.cs calls
// only Decisya.Api.Authentication's own entry points, and never references JwtBearer or
// Microsoft.IdentityModel types directly (NetArchTest boundary rule).
// #21 (0.09): the Tenancy module, the request-scoped caller context and its membership gate,
// and the concurrency-conflict exception handler. Program.cs still calls only entry points:
// AddApiAuthentication, AddTenancyModule, UseCallerContext, UseTenancyMembership,
// MapTenancyEndpoints.
using Decisya.Api.Authentication;
using Decisya.Api.Errors;
using Decisya.Modules.Admin;
using Decisya.Modules.Audit;
using Decisya.Modules.Entitlements;
using Decisya.Modules.Tenancy;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// G2 (#21): registered before AddProblemDetails() so a DbUpdateConcurrencyException is always
// reported as the generic 404 this handler writes, never the host's own fallback ProblemDetails
// behaviour.
builder.Services.AddExceptionHandler<ConcurrencyConflictExceptionHandler>();
builder.Services.AddProblemDetails();

builder.AddApiAuthentication();

builder.Services.AddTenancyModule(builder.Configuration.GetConnectionString("tenancy")!);
builder.Services.AddAuditModule();
builder.Services.AddEntitlementsModule(builder.Configuration.GetConnectionString("entitlements")!);
builder.Services.AddAdminModule();

// G3 G4-21-01 (closes #22 B-1): DI scope validation in every environment, not just
// Development, so a singleton that captures a scoped service (ICurrentTenant, ICurrentCaller,
// TenancyDbContext) fails the build instead of silently sharing one tenant's data across
// concurrent requests.
builder.Host.UseDefaultServiceProvider(options =>
{
    options.ValidateScopes = true;
    options.ValidateOnBuild = true;
});

var app = builder.Build();

// G2 middleware order: UseExceptionHandler first, in every environment (Story 4; the second
// layer behind #19's G4-19-02), then authentication, then the caller context (#21; a malformed
// tenant_id or an untrustworthy identity is rejected here, before routing), then routing
// (explicit, so that rejection runs before endpoint selection), then the Tenancy membership
// gate (JIT provisioning; fail-closed for every endpoint that has not opted out), then
// authorization.
// #25 (0.13): UseNoStoreResponses goes before everything, so every response carries
// Cache-Control: no-store, including the 401 challenge and the exception handler's 500.
app.UseNoStoreResponses();
app.UseExceptionHandler();

app.UseAuthentication();
app.UseCallerContext();
app.UseRouting();
app.UseTenancyMembership();
app.UseAuthorization();

app.MapDefaultEndpoints();
app.MapWhoAmI();
app.MapTenancyEndpoints();

// G3 G4-25-02: the policy (Admin.PlatformAdmin) is on this group inside MapAdminEndpoints, and the
// membership opt-out goes on the same group here, so the two stay coupled: a tenant caller's 403
// runs no database command, and no tenant-data route carries the skip without an admin-only policy.
app.MapAdminEndpoints().SkipTenantMembership();

app.Run();
