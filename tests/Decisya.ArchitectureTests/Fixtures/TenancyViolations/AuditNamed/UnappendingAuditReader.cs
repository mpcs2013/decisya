using Decisya.SharedKernel.Tenancy;

// The namespace deliberately starts with Decisya.Modules.Audit: G3 S-1. Living in an "Audit" namespace
// must not exempt an [AllowCrossTenant] type; only the AuditWriter type of the Decisya.Modules.Audit assembly is exempt.
namespace Decisya.Modules.Audit.Fixtures;

/// <summary>#24 G3 S-1 red fixture: an attributed type in an Audit-named namespace that does not append.</summary>
[AllowCrossTenant("A future audited reader (#25). Must still reference IAuditWriter.AppendAsync, namespace or not.")]
public static class UnappendingAuditReader
{
    public static int Read() => 0;
}
