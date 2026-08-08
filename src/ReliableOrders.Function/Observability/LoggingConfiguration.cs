using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using ReliableOrders.Core.Observability;

namespace ReliableOrders.Function.Observability;

/// <summary>
/// Configures where and how this service's log lines are written.
/// </summary>
/// <remarks>
/// Separate from the rest of the composition root so that logging is configured before anything that
/// might need to report a failure while starting up, and so the arrangement can be exercised on its
/// own by a test that reads the bytes a real provider produces.
/// </remarks>
public static class LoggingConfiguration
{
    /// <summary>
    /// Sends JSON log lines to standard output, with scopes flattened into each line.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Standard output is the whole transport. The Lambda execution environment forwards it to
    /// CloudWatch Logs, so nothing here opens a connection or calls a CloudWatch API — a synchronous
    /// PutLogEvents on the record path would add its own latency and its own failure mode to work that
    /// is already being measured for a deadline.
    /// </para>
    /// <para>
    /// Lines are written by <see cref="SynchronousConsoleLoggerProvider"/> rather than by the
    /// framework's console provider, which queues to a background thread that a Lambda freeze can
    /// strand. That decision is argued where the provider is defined.
    /// </para>
    /// <para>
    /// Retention is not set here and cannot be. A log group's retention is a property of the group,
    /// which CDK creates, so it is configured in infrastructure alongside the function. Without it the
    /// group defaults to never expiring, which is both a cost and a data-retention exposure; the
    /// requirement is recorded in the Lambda Function section of docs/infrastructure.md.
    /// </para>
    /// </remarks>
    /// <param name="builder">The logging builder from the composition root.</param>
    /// <param name="minimumLevel">
    /// The lowest level written. Information by default: the record events in
    /// <see cref="LogEvents"/> are the operational record of what the service did, and dropping them
    /// would leave a duplicate-heavy replay indistinguishable from an idle queue.
    /// </param>
    /// <returns>The same builder, so configuration can be chained.</returns>
    public static ILoggingBuilder AddJsonStdoutLogging(
        this ILoggingBuilder builder,
        LogLevel minimumLevel = LogLevel.Information)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ClearProviders();
        builder.SetMinimumLevel(minimumLevel);

        // Nothing under this prefix is worth a line per invocation. The SDK logs request and response
        // detail at Information, which is both the largest single contributor to log volume and the
        // most likely place for an item's contents to arrive from outside this service's own
        // statements. Warning keeps retries and throttling visible without either.
        builder.AddFilter(AwsCategoryPrefix, LogLevel.Warning);

        builder.AddProvider(new SynchronousConsoleLoggerProvider(Console.Out, new FlatJsonConsoleFormatter(
            new ConsoleFormatterOptions
            {
                IncludeScopes = true,
                UseUtcTimestamp = true,

                // Round-trippable and sortable as text. CloudWatch stamps its own ingestion time on
                // every record, which is when the line arrived rather than when the work happened;
                // under a cold start or a full batch those differ by enough to matter when
                // reconstructing an incident.
                TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ",
            })));

        return builder;
    }

    /// <summary>
    /// The category prefix every AWS SDK and Lambda library logger falls under.
    /// </summary>
    private const string AwsCategoryPrefix = "Amazon";
}
