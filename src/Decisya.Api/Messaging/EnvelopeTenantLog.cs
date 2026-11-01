namespace Decisya.Api.Messaging;

/// <summary>
/// The compiled log message <see cref="EnvelopeTenantMiddleware"/> writes (CA1848). One event per
/// refused message: the resolved handler chain's CLR message type (capped), the message id, the
/// reason word and the trace id. Never the payload, the raw tenant value, the caller id or an
/// exception (G1 Story 1; G3 T-13).
/// </summary>
internal static partial class EnvelopeTenantLog
{
    [LoggerMessage(
        EventId = 6000,
        Level = LogLevel.Warning,
        Message = "Message refused: the envelope has no valid tenant or caller. MessageType={MessageType} MessageId={MessageId} Reason={Reason} TraceId={TraceId}")]
    internal static partial void EnvelopeRefused(
        ILogger logger, string messageType, Guid messageId, string reason, string? traceId);
}
