namespace Decisya.SharedKernel.Results;

/// <summary>
/// A coarse classification of a <see cref="DomainError"/>, for a later HTTP-status mapping
/// (issue #20). SharedKernel deliberately maps no category to a status code itself.
/// </summary>
public enum ErrorCategory
{
    /// <summary>A generic, otherwise unclassified failure. The default when unspecified.</summary>
    Failure = 0,

    /// <summary>Input failed a validation rule.</summary>
    Validation = 1,

    /// <summary>The requested resource does not exist, or does not exist for this tenant.</summary>
    NotFound = 2,

    /// <summary>The operation conflicts with the current state (e.g. a concurrency conflict).</summary>
    Conflict = 3,

    /// <summary>The caller is not permitted to perform the operation.</summary>
    Forbidden = 4,
}
