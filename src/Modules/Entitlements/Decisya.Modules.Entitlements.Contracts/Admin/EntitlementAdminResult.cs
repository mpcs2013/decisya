namespace Decisya.Modules.Entitlements.Contracts.Admin;

/// <summary>
/// The expected outcome of an <see cref="IEntitlementAdminCommands"/> call (issue #25, G2). A
/// contract-local type: Contracts assemblies never expose <c>Decisya.SharedKernel.Results</c>
/// (#32 carry). <see cref="Code"/> is one of <see cref="EntitlementAdminErrorCodes"/> and is
/// <see langword="null"/> exactly when <see cref="Status"/> is <see cref="EntitlementAdminStatus.Succeeded"/>.
/// No member ever carries a message, a reason, a row or a database detail.
/// </summary>
public sealed record EntitlementAdminResult
{
    private EntitlementAdminResult(EntitlementAdminStatus status, string? code)
    {
        Status = status;
        Code = code;
    }

    /// <summary>The outcome class.</summary>
    public EntitlementAdminStatus Status { get; }

    /// <summary>The stable error code, or <see langword="null"/> on success.</summary>
    public string? Code { get; }

    /// <summary>The one success value.</summary>
    public static EntitlementAdminResult Succeeded { get; } = new(EntitlementAdminStatus.Succeeded, null);

    /// <summary>A failure with its status and stable code.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="status"/> is <see cref="EntitlementAdminStatus.Succeeded"/> or not a defined value.</exception>
    /// <exception cref="ArgumentException"><paramref name="code"/> is null, empty or whitespace.</exception>
    public static EntitlementAdminResult Failed(EntitlementAdminStatus status, string code)
    {
        if (status == EntitlementAdminStatus.Succeeded || !Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status), "A failed admin result needs a defined failure status.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        return new EntitlementAdminResult(status, code);
    }
}
