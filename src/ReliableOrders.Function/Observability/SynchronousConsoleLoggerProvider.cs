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
    private readonly TextWriter output;
    private readonly ConsoleFormatter formatter;

    /// <remarks>
    /// One line is one CloudWatch record, so two threads writing at once would produce interleaved
    /// halves that parse as neither. Records are processed sequentially today and the batch handler is
    /// expected to gain bounded parallelism, so the lock is what keeps that change from turning log
    /// lines into fragments.
    /// </remarks>
    private readonly Lock gate = new();

    private IExternalScopeProvider scopeProvider = new LoggerExternalScopeProvider();

    /// <summary>
    /// Creates a provider writing through the given formatter.
    /// </summary>
    /// <param name="output">Where lines are written. Standard output in the function.</param>
    /// <param name="formatter">Decides the shape of each line.</param>
    public SynchronousConsoleLoggerProvider(TextWriter output, ConsoleFormatter formatter)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(formatter);

        this.output = output;
        this.formatter = formatter;
    }

    /// <inheritdoc/>
    public ILogger CreateLogger(string categoryName) => new SynchronousConsoleLogger(this, categoryName);

    /// <inheritdoc/>
    public void SetScopeProvider(IExternalScopeProvider scopeProvider)
    {
        ArgumentNullException.ThrowIfNull(scopeProvider);

        this.scopeProvider = scopeProvider;
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

    private void Write<TState>(in LogEntry<TState> entry)
    {
        lock (this.gate)
        {
            this.formatter.Write(in entry, this.scopeProvider, this.output);
            this.output.Flush();
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
            provider.scopeProvider.Push(state);

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
