// Decisya.Api skeleton (issue #15). AddServiceDefaults and the health endpoints only:
// no authentication, no business endpoint, no DbContext. #20 (0.08) adds JWT validation
// and the first business endpoints here.
var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

var app = builder.Build();

app.MapDefaultEndpoints();

app.Run();
