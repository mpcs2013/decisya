using Decisya.SharedKernel.Tenancy;

namespace Decisya.Modules.Audit.Application;

/// <summary>#24 G3 S-1 red fixture: the real writer's full name (<c>Decisya.Modules.Audit.Application.AuditWriter</c>) in the wrong assembly. Not exempt.</summary>
[AllowCrossTenant("Spoofs the real writer's full name from a different assembly; the exemption is assembly-qualified.")]
public static class AuditWriter
{
    public static int Noop() => 0;
}
