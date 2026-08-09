using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace ReliableOrders.Function.Observability;

/// <summary>
/// Writes each log line to standard output on the thread that logged it.
/// </summary>
/// <remarks>
/// <para>
/// The framework's console provider queues lines to a background thread. That is the right trade for
/// a long-running host, where the writing thread is a bottleneck and the queue drains continuously.
/// It is the wrong one here: Lambda freezes the execution environment the moment the handler returns,
/// so anything still queued is not written until the next invocation thaws the sandbox, and is lost
/// outright when the environment is reclaimed instead. The lines at risk are the last ones written,
/// which is exactly where <c>BatchCompleted</c> lands — the one line that says a partial batch failure
/// happened, on an invocation Lambda otherwise reports as a success.
/// </para>
/// <para>
/// Writing synchronously also keeps log lines and the metric records from
/// <c>EmbeddedMetricsPublisher</c> in the order they happened. The publisher writes and flushes
/// synchronously, so a queued logger would interleave the two by when each was drained rather than by
/// when each occurred, and an incident reconstructed from that ordering would be wrong.
/// </para>
/// <para>
/// The cost is real and accepted: a record's processing now includes the time to serialise and write
/// its own log lines. At the volume one invocation produces, that is a few writes to a pipe the
/// runtime is draining anyway, and it is paid inside the invocation that caused it rather than
/// charged to whichever one thaws next.
/// </para>
/// </remarks>
[ProviderAlias("SynchronousConsole")]
public sealed class SynchronousConsoleLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly TextWriter _output;
    private readonly ConsoleFormatter _formatter;

    /// <remarks>
    /// One line is one CloudWatch record, so two threads writing at once would produce interleaved
    /// halves that parse as neither. Records are processed sequentially today and the batch handler is
    /// expected to gain bounded parallelism, so the lock is what keeps that change from turning log
    /// lines into fragments.
    /// </remarks>
    private readonly Lock _gate = new();

    private IExternalScopeProvider _scopeProvider = new LoggerExternalScopeProvider();

    /// <summary>
    /// Creates a provider writing through the given formatter.
    /// </summary>
    /// <param name="output">Where lines are written. Standard output in the function.</param>
    /// <param name="formatter">Decides the shape of each line.</param>
    public SynchronousConsoleLoggerProvider(TextWriter output, ConsoleFormatter formatter)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(formatter);

        _output = output;
        _formatter = formatter;
    }

    /// <inheritdoc/>
    public ILogger CreateLogger(string categoryName) => new SynchronousConsoleLogger(this, categoryName);

    /// <inheritdoc/>
    public void SetScopeProvider(IExternalScopeProvider scopeProvider)
    {
        ArgumentNullException.ThrowIfNull(scopeProvider);

        _scopeProvider = scopeProvider;
    }

    /// <summary>
    /// Nothing to release.
    /// </summary>
    /// <remarks>
    /// There is no queue to drain, which is the point. The writer is owned by whoever supplied it —
    /// standard output in the function — and closing it here would silence every later line.
    /// </remarks>
    public void Dispose()
    {
    }

    /// <remarks>
    /// Formatting runs on the caller's thread, which is the whole point of this provider and also the
    /// one thing it has to make safe. The framework's provider formats on a background thread, where a
    /// formatter failure can never reach the work being logged about; here an exception would leave
    /// <see cref="ILogger.Log"/> throwing into record processing, and a handler would classify a
    /// committed order as a failed one and return it for redelivery. A state value whose
    /// <c>ToString</c> throws is enough to cause it. Telemetry does not get to fail the work it
    /// describes.
    /// </remarks>
    private void Write<TState>(in LogEntry<TState> entry)
    {
        lock (_gate)
        {
            try
            {
                _formatter.Write(in entry, _scopeProvider, _output);
                _output.Flush();
            }
#pragma warning disable CA1031 // Anything the formatter throws has to stop here, whatever it is.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                ReportDroppedLine(exception);
            }
        }
    }

    /// <summary>
    /// Says that a line was dropped, in as few moving parts as possible.
    /// </summary>
    /// <remarks>
    /// Swallowing outright would leave an operator reading a log with holes in it and no way to know.
    /// This writes no scope, no state and no message text — only the exception's type, which is a CLR
    /// type name and so contains nothing JSON would need escaped. If even that fails there is nothing
    /// left to try, and the alternative to giving up is a formatter failure taking down the record.
    /// </remarks>
    private void ReportDroppedLine(Exception exception)
    {
        try
        {
            _output.WriteLine(
                "{\"LogLevel\":\"Error\",\"Category\":\"" + typeof(SynchronousConsoleLoggerProvider).FullName
                + "\",\"Message\":\"A log line could not be formatted and was dropped\",\"ExceptionType\":\""
                + exception.GetType().FullName + "\"}");

            _output.Flush();
        }
#pragma warning disable CA1031 // Last resort. There is no further place to report a failure to.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    /// <remarks>
    /// Level filtering is not decided here. <c>LoggerFactory</c> wraps this logger with the filters
    /// configured on the builder, so answering true to <see cref="ILogger.IsEnabled"/> means "this
    /// provider can write anything", not "everything is written".
    /// </remarks>
    private sealed class SynchronousConsoleLogger(SynchronousConsoleLoggerProvider provider, string category)
        : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull =>
            provider._scopeProvider.Push(state);

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            provider.Write(new LogEntry<TState>(logLevel, category, eventId, state, exception, formatter));
    }
}
