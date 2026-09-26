using OpenTelemetry;
using OpenTelemetry.Logs;

namespace Decisya.ServiceDefaults.Logging;

/// <summary>
/// The OTLP side of the logging pipeline: rewrites every <see cref="LogRecord"/> through
/// <see cref="SensitiveDataMaskingProcessor"/> before the OTLP batch export processor sees
/// it.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="OnEnd"/> runs synchronously on the logging thread, ahead of the exporter's
/// bounded queue, so the <see cref="AsyncLocal{T}"/> behind
/// <see cref="ILogEnrichmentContext"/> is still the caller's.
/// </para>
/// <para>
/// The exception is moved out of <see cref="LogRecord.Exception"/> into three masked
/// <c>exception.*</c> attributes. Leaving the exception object in place would let the
/// exporter render its unmasked message and stack trace itself.
/// </para>
/// </remarks>
internal sealed class MaskingLogRecordProcessor(
    SensitiveDataMaskingProcessor masking,
    ILogEnrichmentContext enrichment) : BaseProcessor<LogRecord>
{
    internal const string ExceptionTypeKey = "exception.type";
    internal const string ExceptionMessageKey = "exception.message";
    internal const string ExceptionStackTraceKey = "exception.stacktrace";
    internal const string TenantIdKey = "tenant_id";
    internal const string UserIdKey = "user_id";

    public override void OnEnd(LogRecord data)
    {
        ArgumentNullException.ThrowIfNull(data);

        try
        {
            var masked = masking.Process(data.Attributes);
            var attributes = new List<KeyValuePair<string, object?>>(masked.Attributes.Count + 6);
            attributes.AddRange(masked.Attributes);

            if (data.Exception is { } exception)
            {
                var (type, message, stackTrace) = masking.ProcessException(exception);
                attributes.Add(new KeyValuePair<string, object?>(ExceptionTypeKey, type));
                attributes.Add(new KeyValuePair<string, object?>(ExceptionMessageKey, message));
                attributes.Add(new KeyValuePair<string, object?>(ExceptionStackTraceKey, stackTrace));
                data.Exception = null;
            }

            if (enrichment.TenantId is { } tenantId)
            {
                attributes.Add(new KeyValuePair<string, object?>(TenantIdKey, tenantId));
            }

            if (enrichment.UserIdHash is { } userIdHash)
            {
                attributes.Add(new KeyValuePair<string, object?>(UserIdKey, userIdHash));
            }

            if (masked.Template is { } template)
            {
                // Kept so the exporter can use it as the body once the formatted message
                // (which would contain the unmasked values) is dropped below.
                attributes.Add(new KeyValuePair<string, object?>(
                    SensitiveDataMaskingProcessor.OriginalFormatKey, template));
            }

            data.Attributes = attributes;

            if (masked.AnyMasked)
            {
                data.FormattedMessage = masked.Template is null ? SensitiveDataMaskingProcessor.Mask : null;
            }
        }
        catch (Exception)
        {
            ObservabilityDiagnostics.RecordMaskingError("otlp");
            data.Attributes =
            [
                new KeyValuePair<string, object?>(SensitiveDataMaskingProcessor.MaskingErrorKey, true),
            ];
            data.FormattedMessage = null;
            data.Exception = null;
        }
    }
}
