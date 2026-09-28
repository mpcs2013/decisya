// #20 (0.08): JWT bearer validation, the fallback authorization policy, generic exception
// handling in every environment, and the first endpoint (GET /api/whoami). Program.cs calls
// only Decisya.Api.Authentication's own entry points, and never references JwtBearer or
// Microsoft.IdentityModel types directly (NetArchTest boundary rule).
using Decisya.Api.Authentication;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddProblemDetails();

builder.AddApiAuthentication();

var app = builder.Build();

// G2 middleware order: UseExceptionHandler first, in every environment (Story 4; the second
// layer behind #19's G4-19-02), then authentication, then authorization, then endpoints.
app.UseExceptionHandler();

app.UseAuthentication();
app.UseAuthorization();

app.MapDefaultEndpoints();
app.MapWhoAmI();

app.Run();
