using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using NodaTime;
using NodaTime.Text;

namespace Decisya.ServiceDefaults.Logging;

/// <summary>
/// Writes one well-formed JSON object per log record to stdout, with a fixed field set,
/// trace correlation, and every value run through <see cref="SensitiveDataMaskingProcessor"/>.
/// </summary>
/// <remarks>
/// <para>
/// The writer uses <see cref="JavaScriptEncoder.Default"/>, which escapes control
/// characters, newlines and everything outside Basic Latin. One record is therefore always
/// exactly one line, and neither a message, a key, a category, a scope nor an exception
/// text can forge a second line or a second top-level field. The unsafe relaxed encoder is
/// never used.
/// </para>
/// <para>
/// <see cref="Activity.Current"/> is read here, on the logging thread:
/// <c>ConsoleLogger.Log</c> calls the formatter synchronously and only queues the finished
/// string, so the ambient activity is still the caller's.
/// </para>
/// </remarks>
public sealed class DecisyaJsonConsoleFormatter : ConsoleFormatter
{
    /// <summary>The formatter name the console provider is pinned to.</summary>
    public const string FormatterName = "decisya-json";

    internal const string ScopeAttributeKey = "scope";
    internal const string FormatterErrorValue = "formatter";

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = false,
        Encoder = JavaScriptEncoder.Default,
        SkipValidation = false,
    };

    private readonly IClock _clock;
    private readonly SensitiveDataMaskingProcessor _masking;
    private readonly ILogEnrichmentContext _enrichment;

    public DecisyaJsonConsoleFormatter(
        IClock clock,
        SensitiveDataMaskingProcessor masking,
        ILogEnrichmentContext enrichment)
        : base(FormatterName)
    {
        _clock = clock;
        _masking = masking;
        _enrichment = enrichment;
    }

    public override void Write<TState>(
        in LogEntry<TState> logEntry,
        IExternalScopeProvider? scopeProvider,
        TextWriter textWriter)
    {
        ArgumentNullException.ThrowIfNull(textWriter);

        string? line;
        try
        {
            line = FormatRecord(in logEntry, scopeProvider);
        }
        catch (Exception)
        {
            line = null;
        }

        if (line is null)
        {
            ObservabilityDiagnostics.RecordMaskingError(FormatterErrorValue);
            try
            {
                line = FormatFallback(logEntry.LogLevel, logEntry.Category, logEntry.EventId);
            }
            catch (Exception)
            {
                return;
            }
        }

        try
        {
            textWriter.Write(line);
        }
        catch (Exception)
        {
            // A log line is never worth failing the caller's request for.
        }
    }

    private string FormatRecord<TState>(in LogEntry<TState> logEntry, IExternalScopeProvider? scopeProvider)
    {
        var state = logEntry.State as IReadOnlyList<KeyValuePair<string, object?>>;
        var masked = _masking.Process(state);

        string? message;
        if (masked.AnyMasked)
        {
            message = masked.Template ?? SensitiveDataMaskingProcessor.Mask;
        }
        else
        {
            message = logEntry.Formatter?.Invoke(logEntry.State, logEntry.Exception);
        }

        var buffer = new ArrayBufferWriter<byte>(512);
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();

            WriteHeader(writer, logEntry.LogLevel, logEntry.Category, logEntry.EventId);
            WriteNullableString(writer, "message", message);
            WriteNullableString(writer, "message_template", masked.Template);
            WriteTraceContext(writer);
            WriteNullableString(writer, "tenant_id", _enrichment.TenantId);
            WriteNullableString(writer, "user_id", _enrichment.UserIdHash);
            WriteAttributes(writer, masked, scopeProvider);

            if (logEntry.Exception is { } exception)
            {
                WriteNullableString(writer, "exception", _masking.ProcessException(exception).StackTrace);
            }
            else
            {
                writer.WriteNull("exception");
            }

            writer.WriteEndObject();
        }

        return string.Concat(Encoding.UTF8.GetString(buffer.WrittenSpan), "\n");
    }

    private string FormatFallback(LogLevel level, string category, EventId eventId)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();

            WriteHeader(writer, level, Sanitize(category), new EventId(eventId.Id, Sanitize(eventId.Name)));
            writer.WriteNull("message");
            writer.WriteNull("message_template");
            WriteTraceContext(writer);
            WriteNullableString(writer, "tenant_id", Sanitize(_enrichment.TenantId));
            WriteNullableString(writer, "user_id", Sanitize(_enrichment.UserIdHash));

            writer.WritePropertyName("attributes");
            writer.WriteStartObject();
            writer.WriteString(SensitiveDataMaskingProcessor.MaskingErrorKey, FormatterErrorValue);
            writer.WriteEndObject();

            writer.WriteNull("exception");
            writer.WriteEndObject();
        }

        return string.Concat(Encoding.UTF8.GetString(buffer.WrittenSpan), "\n");
    }

    private void WriteHeader(Utf8JsonWriter writer, LogLevel level, string? category, EventId eventId)
    {
        writer.WriteString("timestamp", InstantPattern.ExtendedIso.Format(_clock.GetCurrentInstant()));
        writer.WriteString("level", LevelName(level));
        WriteNullableString(writer, "category", category);
        writer.WriteNumber("event_id", eventId.Id);
        WriteNullableString(writer, "event_name", eventId.Name);
    }

    private static void WriteTraceContext(Utf8JsonWriter writer)
    {
        var activity = Activity.Current;
        WriteNullableString(writer, "trace_id", activity?.TraceId.ToHexString());
        WriteNullableString(writer, "span_id", activity?.SpanId.ToHexString());
    }

    private void WriteAttributes(Utf8JsonWriter writer, MaskedState masked, IExternalScopeProvider? scopeProvider)
    {
        writer.WritePropertyName("attributes");
        writer.WriteStartObject();

        var written = new HashSet<string>(StringComparer.Ordinal);

        foreach (var pair in masked.Attributes)
        {
            WriteAttribute(writer, written, pair.Key, pair.Value);
        }

        scopeProvider?.ForEachScope(
            static (scope, carrier) => carrier.Formatter.WriteScope(carrier.Writer, carrier.Written, scope),
            (Writer: writer, Written: written, Formatter: this));

        writer.WriteEndObject();
    }

    private void WriteScope(Utf8JsonWriter writer, HashSet<string> written, object? scope)
    {
        if (scope is IEnumerable<KeyValuePair<string, object?>> pairs)
        {
            foreach (var pair in pairs)
            {
                if (string.Equals(pair.Key, SensitiveDataMaskingProcessor.OriginalFormatKey, StringComparison.Ordinal))
                {
                    continue;
                }

                WriteAttribute(writer, written, pair.Key ?? string.Empty, _masking.ProcessValue(pair.Key ?? string.Empty, pair.Value, out _));
            }

            return;
        }

        WriteAttribute(writer, written, ScopeAttributeKey, _masking.ProcessValue(ScopeAttributeKey, scope, out _));
    }

    private static void WriteAttribute(Utf8JsonWriter writer, HashSet<string> written, string key, object? value)
    {
        if (!written.Add(key))
        {
            // State wins over a scope carrying the same key, and one key is written once.
            return;
        }

        writer.WritePropertyName(key);
        WriteValue(writer, value);
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                return;
            case string text:
                writer.WriteStringValue(text);
                return;
            case bool flag:
                writer.WriteBooleanValue(flag);
                return;
            case int number:
                writer.WriteNumberValue(number);
                return;
            case long number:
                writer.WriteNumberValue(number);
                return;
            case short number:
                writer.WriteNumberValue(number);
                return;
            case byte number:
                writer.WriteNumberValue(number);
                return;
            case sbyte number:
                writer.WriteNumberValue(number);
                return;
            case uint number:
                writer.WriteNumberValue(number);
                return;
            case ulong number:
                writer.WriteNumberValue(number);
                return;
            case ushort number:
                writer.WriteNumberValue(number);
                return;
            case decimal number:
                writer.WriteNumberValue(number);
                return;
            case double number:
                // A non-finite value has no JSON number form; writing it as a number would
                // throw and cost the whole line.
                if (double.IsFinite(number))
                {
                    writer.WriteNumberValue(number);
                }
                else
                {
                    writer.WriteStringValue(number.ToString(CultureInfo.InvariantCulture));
                }

                return;
            case float number:
                if (float.IsFinite(number))
                {
                    writer.WriteNumberValue(number);
                }
                else
                {
                    writer.WriteStringValue(number.ToString(CultureInfo.InvariantCulture));
                }

                return;
            default:
                writer.WriteStringValue(SafeToString(value));
                return;
        }
    }

    private static string SafeToString(object value)
    {
        try
        {
            return value is IFormattable formattable
                ? formattable.ToString(null, CultureInfo.InvariantCulture) ?? SensitiveDataMaskingProcessor.Mask
                : value.ToString() ?? SensitiveDataMaskingProcessor.Mask;
        }
        catch (Exception)
        {
            ObservabilityDiagnostics.RecordMaskingError("tostring");
            return SensitiveDataMaskingProcessor.Mask;
        }
    }

    private static void WriteNullableString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }

    private static string LevelName(LogLevel level) => level switch
    {
        LogLevel.Trace => nameof(LogLevel.Trace),
        LogLevel.Debug => nameof(LogLevel.Debug),
        LogLevel.Information => nameof(LogLevel.Information),
        LogLevel.Warning => nameof(LogLevel.Warning),
        LogLevel.Error => nameof(LogLevel.Error),
        LogLevel.Critical => nameof(LogLevel.Critical),
        LogLevel.None => nameof(LogLevel.None),
        _ => level.ToString(),
    };

    /// <summary>
    /// Replaces unpaired surrogates with U+FFFD. Used only on the fallback path, whose one
    /// job is to produce a parseable line no matter what the failing record contained.
    /// </summary>
    private static string? Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        char[]? repaired = null;
        for (var i = 0; i < value.Length; i++)
        {
            var character = value[i];
            var paired = char.IsHighSurrogate(character)
                && i + 1 < value.Length
                && char.IsLowSurrogate(value[i + 1]);

            if (paired)
            {
                i++;
                continue;
            }

            if (!char.IsSurrogate(character))
            {
                continue;
            }

            repaired ??= value.ToCharArray();
            repaired[i] = '�';
        }

        return repaired is null ? value : new string(repaired);
    }
}
