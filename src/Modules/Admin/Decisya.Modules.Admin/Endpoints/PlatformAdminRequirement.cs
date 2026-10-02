using Microsoft.AspNetCore.Authorization;

namespace Decisya.Modules.Admin.Endpoints;

/// <summary>Marker requirement for <see cref="AdminModule.PlatformAdminPolicy"/>. Carries no data: the handler reads the caller context.</summary>
internal sealed class PlatformAdminRequirement : IAuthorizationRequirement;
