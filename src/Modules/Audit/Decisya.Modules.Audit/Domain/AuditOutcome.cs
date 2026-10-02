namespace Decisya.Modules.Audit.Domain;

/// <summary>The outcome stored on a record. Only <see cref="Succeeded"/> is audited in #24 (G1 Q2); a refusal writes nothing.</summary>
internal enum AuditOutcome
{
    Succeeded = 1,
}
