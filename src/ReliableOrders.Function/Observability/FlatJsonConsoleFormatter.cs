using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using ReliableOrders.Core.Observability;

namespace ReliableOrders.Function.Observability;

/// <summary>
/// Writes each log record as one line of JSON with every scope field at the top level.
/// </summary>
/// <remarks>
/// <para>
/// The framework's own JSON formatter nests scopes in a <c>Scopes</c> array, which puts a field at
/// <c>Scopes.1.OrderId</c> and moves it to <c>Scopes.2.OrderId</c> on any line where an extra scope
/// happens to be open. This service opens the order identity scope only after a body parses, so the
/// index differs between a parse failure and a success and a single Logs Insights query cannot match
/// both. Flattening puts every field at a fixed path, which is what "logs are queryable by event,
/// order, correlation and SQS message ID" actually requires.
/// </para>
/// <para>
/// Fields are gathered into a dictionary before anything is written, so a name can never appear
/// twice in one object. Precedence runs outer scope, then inner scope, then the record's own state:
/// the nearest writer to the event wins, which is what a reader would assume.
/// </para>
/// <para>
/// The framework's numeric event identifier is written as <c>LogEventId</c>, not <c>EventId</c>.
/// <c>EventId</c> is reserved for the publisher's order event identifier per the Logging
/// Specification, and letting a log statement's number land on that name would silently replace a
/// UUID with a small integer on every line that carries both. <see cref="LogEvents"/> supplies a text
/// name alongside it so the two are never confused by eye either.
/// </para>
/// </remarks>
public sealed class FlatJsonConsoleFormatter : ConsoleFormatter
{
    /// <summary>
    /// The name this formatter is known by.
    /// </summary>
    public const string FormatterName = "reliable-orders-json";

    private readonly ConsoleFormatterOptions _options;

    /// <summary>
    /// Creates the formatter.
    /// </summary>
    /// <remarks>
    /// Takes the options directly rather than an <c>IOptionsMonitor</c>. Reload exists so a long-lived
    /// host can change logging without restarting; a Lambda execution environment is replaced rather
    /// than reconfigured, so the machinery would only ever deliver the value it started with.
    /// </remarks>
    /// <param name="options">How each line is written.</param>
    public FlatJsonConsoleFormatter(ConsoleFormatterOptions options)
        : base(FormatterName)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
    }

    /// <inheritdoc/>
    public override void Write<TState>(
        in LogEntry<TState> logEntry,
        IExternalScopeProvider? scopeProvider,
        TextWriter textWriter)
    {
        ArgumentNullException.ThrowIfNull(textWriter);

        var message = logEntry.Formatter?.Invoke(logEntry.State, logEntry.Exception);

        if (message is null && logEntry.Exception is null)
        {
            return;
        }

        var fields = new Dictionary<string, object?>(StringComparer.Ordinal);

        if (_options.IncludeScopes)
        {
            CollectScopes(scopeProvider, fields);
        }

        CollectState(logEntry.State, fields);

        var buffer = new ArrayBufferWriter<byte>(InitialLineBytes);

        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();

            if (WriteTimestamp(writer))
            {
                Reserve(TimestampField);
            }

            writer.WriteString(LogLevelField, Describe(logEntry.LogLevel));
            Reserve(LogLevelField);

            writer.WriteNumber(LogEventIdField, logEntry.EventId.Id);
            Reserve(LogEventIdField);

            if (logEntry.EventId.Name is { Length: > 0 } eventName)
            {
                writer.WriteString(LogEventField, eventName);
                Reserve(LogEventField);
            }

            writer.WriteString(CategoryField, logEntry.Category);
            Reserve(CategoryField);

            if (message is not null)
            {
                writer.WriteString(MessageField, message);
                Reserve(MessageField);
            }

            // The type and the stack trace, never the message. "Full exception payloads containing
            // message bodies" are on the Do Not Log list, and an SDK exception message is where a
            // request body or an item's contents would arrive. The type and the throwing frame
            // identify the defect; a cause an operator is meant to read belongs in a fixed-vocabulary
            // Reason field, which is what ProcessingLog writes. A failure that must be human-readable,
            // such as missing cold-start configuration, should therefore be thrown rather than logged
            // — the runtime prints it.
            if (logEntry.Exception is { } exception)
            {
                writer.WriteString(ExceptionTypeField, exception.GetType().FullName);
                Reserve(ExceptionTypeField);

                if (exception.StackTrace is { Length: > 0 } stackTrace)
                {
                    writer.WriteString(ExceptionStackTraceField, stackTrace);
                    Reserve(ExceptionStackTraceField);
                }
            }

            foreach (var field in fields)
            {
                WriteField(writer, field.Key, field.Value);
            }

            writer.WriteEndObject();
            writer.Flush();
        }

        // Moves a field aside when this record has already written a property of that name.
        //
        // Utf8JsonWriter does not reject a duplicate key even with validation on, and a reader's
        // choice between the two is undefined, so one value would be silently unreachable. Renaming
        // rather than dropping keeps it: a line that loses a field an operator went looking for is the
        // same problem in a quieter form. Driven by what was actually written rather than by a fixed
        // list, because half these names are conditional — a record with no timestamp configured
        // leaves Timestamp free, and moving a field out of it would put the value at a path no query
        // written against the production shape will match.
        void Reserve(string name)
        {
            if (fields.Remove(name, out var displaced))
            {
                fields[ReservedFieldPrefix + name] = displaced;
            }
        }

        textWriter.WriteLine(Encoding.UTF8.GetString(buffer.WrittenSpan));
    }

    /// <summary>
    /// The record's own template fields, which win over any scope of the same name.
    /// </summary>
    /// <remarks>
    /// <c>{OriginalFormat}</c> is dropped. It repeats the template that <c>Message</c> already carries
    /// with its values filled in, and per-record log volume is the dominant cost of this project at
    /// any real throughput.
    /// </remarks>
    private static void CollectState<TState>(TState state, Dictionary<string, object?> fields)
    {
        if (state is not IEnumerable<KeyValuePair<string, object?>> pairs)
        {
            return;
        }

        foreach (var pair in pairs)
        {
            if (!IsOriginalFormat(pair.Key))
            {
                fields[pair.Key] = pair.Value;
            }
        }
    }

    /// <remarks>
    /// Scopes arrive outermost first, so assigning in order leaves the innermost value in place.
    /// A scope that is not a set of named values is skipped: it has no field name, so nothing could
    /// query it, and inventing one would put an unqueryable string in every line inside that scope.
    /// </remarks>
    private static void CollectScopes(IExternalScopeProvider? scopeProvider, Dictionary<string, object?> fields) =>
        scopeProvider?.ForEachScope(
            static (scope, target) =>
            {
                if (scope is not IEnumerable<KeyValuePair<string, object>> pairs)
                {
                    return;
                }

                foreach (var pair in pairs)
                {
                    // A scope opened through the BeginScope(string, params object[]) overload carries
                    // the template under this key, the same as a log record's state does. Filtering it
                    // only on the state path would let one such scope reattach it to every line
                    // written inside it, which is the per-record cost the state-side filter avoids.
                    if (!IsOriginalFormat(pair.Key))
                    {
                        target[pair.Key] = pair.Value;
                    }
                }
            },
            fields);

    /// <remarks>
    /// Every numeric type keeps its JSON type so Logs Insights can compare and sum it without a cast.
    /// Missing one is not a compile error and not a run-time error either — it arrives as a quoted
    /// string, and a query that averages it returns no rows rather than failing, which is the silent
    /// empty result this formatter exists to avoid. Anything genuinely non-numeric is written as a
    /// string through the invariant culture, which matters because the process runs with invariant
    /// globalization and a field must not change shape with a locale.
    /// </remarks>
    private static void WriteField(Utf8JsonWriter writer, string name, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNull(name);
                break;
            case string text:
                writer.WriteString(name, text);
                break;
            case bool flag:
                writer.WriteBoolean(name, flag);
                break;
            case int number:
                writer.WriteNumber(name, number);
                break;
            case long number:
                writer.WriteNumber(name, number);
                break;
            // Guarded, because JSON cannot represent NaN or an infinity and Utf8JsonWriter throws
            // rather than approximating one. The formatter runs synchronously inside ILogger.Log, so
            // that exception would leave a log call failing the record it was describing. A rate over
            // an empty batch is enough to produce one; it falls through to the text branch below and
            // is written as "NaN".
            case double number when double.IsFinite(number):
                writer.WriteNumber(name, number);
                break;
            case float number when float.IsFinite(number):
                writer.WriteNumber(name, number);
                break;
            case uint number:
                writer.WriteNumber(name, number);
                break;
            case ulong number:
                writer.WriteNumber(name, number);
                break;
            // Widened rather than given a case each. Utf8JsonWriter has no overload for the narrower
            // integer types, and JSON has one number type regardless.
            case byte or sbyte or short or ushort:
                writer.WriteNumber(name, Convert.ToInt32(value, CultureInfo.InvariantCulture));
                break;
            case decimal number:
                writer.WriteNumber(name, number);
                break;
            case IFormattable formattable:
                writer.WriteString(name, formattable.ToString(null, CultureInfo.InvariantCulture));
                break;
            default:
                writer.WriteString(name, value.ToString());
                break;
        }
    }

    /// <returns>True when a timestamp was written, so the name is reserved only when it is taken.</returns>
    private bool WriteTimestamp(Utf8JsonWriter writer)
    {
        var format = _options.TimestampFormat;

        if (format is null)
        {
            return false;
        }

        var now = _options.UseUtcTimestamp ? DateTimeOffset.UtcNow : DateTimeOffset.Now;

        writer.WriteString(TimestampField, now.ToString(format, CultureInfo.InvariantCulture));

        return true;
    }

    /// <remarks>
    /// Written out rather than taken from <c>ToString</c> so the values are a fixed vocabulary an
    /// alarm can match, and so a future framework rename cannot quietly change what every saved query
    /// filters on.
    /// </remarks>
    private static string Describe(LogLevel level) => level switch
    {
        LogLevel.Trace => "Trace",
        LogLevel.Debug => "Debug",
        LogLevel.Information => "Information",
        LogLevel.Warning => "Warning",
        LogLevel.Error => "Error",
        LogLevel.Critical => "Critical",
        _ => "None",
    };

    private const string TimestampField = "Timestamp";
    private const string LogLevelField = "LogLevel";
    private const string LogEventIdField = "LogEventId";
    private const string LogEventField = "LogEvent";
    private const string CategoryField = "Category";
    private const string MessageField = "Message";
    private const string ExceptionTypeField = "ExceptionType";
    private const string ExceptionStackTraceField = "ExceptionStackTrace";
    private const string OriginalFormatKey = "{OriginalFormat}";

    private static bool IsOriginalFormat(string key) =>
        string.Equals(key, OriginalFormatKey, StringComparison.Ordinal);

    /// <remarks>
    /// An underscore rather than a dot: CloudWatch Logs Insights reads a dot as a path separator and
    /// needs the whole name backquoted to query it, which is a poor thing to discover mid-incident.
    /// </remarks>
    private const string ReservedFieldPrefix = "Field_";

    /// <summary>
    /// Sized for a typical record line so the common case writes without growing the buffer.
    /// </summary>
    private const int InitialLineBytes = 1024;

    /// <remarks>
    /// Not indented: CloudWatch treats a newline as a record boundary, so a pretty-printed object
    /// would arrive as a dozen unparseable records instead of one.
    /// </remarks>
    private static readonly JsonWriterOptions WriterOptions = new() { Indented = false, SkipValidation = false };
}
