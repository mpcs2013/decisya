using Decisya.Modules.Audit.Domain;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Decisya.Modules.Audit.Infrastructure;

/// <summary>Stores an <see cref="AuditOutcome"/> as its code (<c>succeeded</c>), never the enum name.</summary>
internal sealed class AuditOutcomeConverter : ValueConverter<AuditOutcome, string>
{
    internal const string Succeeded = "succeeded";

    public AuditOutcomeConverter()
        : base(outcome => ToCode(outcome), code => FromCode(code))
    {
    }

    internal static string ToCode(AuditOutcome outcome) => outcome switch
    {
        AuditOutcome.Succeeded => Succeeded,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), "The audit outcome is not defined."),
    };

    internal static AuditOutcome FromCode(string code) => code switch
    {
        Succeeded => AuditOutcome.Succeeded,
        _ => throw new InvalidOperationException("The stored audit outcome code is not recognised."),
    };
}
