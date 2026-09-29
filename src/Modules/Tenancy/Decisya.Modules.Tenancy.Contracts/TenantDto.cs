using Decisya.SharedKernel.Tenancy;

namespace Decisya.Modules.Tenancy.Contracts;

/// <summary>The caller's own tenant, as returned by <c>GET /api/tenancy/me</c> (issue #21, G2).</summary>
public sealed record TenantDto(TenantId Id);
