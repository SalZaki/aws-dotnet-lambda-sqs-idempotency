using System.Globalization;
using Amazon.CDK;
using Amazon.CDK.AWS.Lambda;
using Amazon.CDK.AWS.Lambda.EventSources;
using Amazon.CDK.AWS.Logs;
using Constructs;
using ReliableOrders.Cdk.Configuration;

namespace ReliableOrders.Cdk.Constructs;

/// <summary>
/// The function that processes orders, and the event source that feeds it from the queue.
/// </summary>
/// <remarks>
/// <para>
/// The runtime identifier comes from <see cref="EnvironmentConfig"/> rather than a CDK constant, so
/// falling back to an earlier managed runtime is a configuration change rather than an edit here. The
/// identifier has to exist in the target Region before a deployment can succeed, which is checked by
/// deploying rather than by synthesising.
/// </para>
/// <para>
/// The code asset is supplied rather than built. Where the publish output comes from is the
/// composition root's problem, and passing it in is what lets the tests synthesise this construct
/// without publishing the function first.
/// </para>
/// </remarks>
public sealed class OrderProcessorConstruct : Construct
{
    /// <summary>Assembly, type and method, which is how the managed runtime finds the entry point.</summary>
    public const string Handler = "ReliableOrders.Function::ReliableOrders.Function.Function::HandleAsync";

    /// <summary>
    /// The service name every log line and metric is stamped with.
    /// </summary>
    /// <remarks>
    /// A constant rather than configuration. It names the service, which does not vary between
    /// deployments of it — the environment is what distinguishes those, and it is its own dimension.
    /// </remarks>
    public const string ServiceName = "reliable-orders";

    /// <summary>The CloudWatch namespace the metrics are published under.</summary>
    public const string MetricsNamespace = "ReliableOrders";

    /// <summary>
    /// Creates the function, its log group and its event source mapping.
    /// </summary>
    /// <param name="scope">The stack these belong to.</param>
    /// <param name="id">The construct identifier, which prefixes the logical IDs.</param>
    /// <param name="config">Runtime, sizing, concurrency and batching.</param>
    /// <param name="code">The published function, as an asset.</param>
    /// <param name="messaging">The queue the event source reads.</param>
    /// <param name="persistence">The tables the function writes, which also grants it the right to.</param>
    public OrderProcessorConstruct(
        Construct scope,
        string id,
        EnvironmentConfig config,
        Code code,
        MessagingConstruct messaging,
        PersistenceConstruct persistence)
        : base(scope, id)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(messaging);
        ArgumentNullException.ThrowIfNull(persistence);

        // Declared rather than left to the runtime, which would create a log group with no expiry and
        // bill for it forever. Its removal policy follows the data, so a destroyed development stack
        // takes its logs with it and a retained environment keeps them for the incident that outlives
        // the deployment.
        var logs = new LogGroup(this, "Logs", new LogGroupProps
        {
            Retention = RetentionDays.ONE_MONTH,
            RemovalPolicy = config.RetainData ? RemovalPolicy.RETAIN : RemovalPolicy.DESTROY,
        });

        Function = new Function(this, "OrderProcessorFunction", new FunctionProps
        {
            Runtime = new Runtime(config.LambdaRuntimeIdentifier, RuntimeFamily.DOTNET_CORE),
            Handler = Handler,
            Code = code,
            MemorySize = config.LambdaMemoryMb,
            Timeout = Duration.Seconds(config.LambdaTimeoutSeconds),
            ReservedConcurrentExecutions = config.ReservedConcurrency,
            LogGroup = logs,

            // Text, although every line this function writes is JSON. The advanced JSON log format
            // wraps each stdout line in an envelope of Lambda's own, which would put the application's
            // fields under a "message" property and, worse, bury the EMF record's "_aws" key one level
            // down. CloudWatch only extracts metrics from "_aws" at the root, so the metrics would stop
            // being published while the log line carrying them still looked right. The function owns
            // its JSON; see FlatJsonConsoleFormatter and EmbeddedMetricsPublisher.
            LoggingFormat = LoggingFormat.TEXT,

            // Off, and stated. The tracing decision in docs/observability.md is that exactly one
            // pipeline is active, and it is OpenTelemetry — X-Ray active tracing here would produce a
            // second, partial trace of the same invocation and bill for it.
            Tracing = Tracing.DISABLED,

            Environment = Variables(config, persistence),
        });

        persistence.GrantOrderTransaction(Function.Role!);

        // The event source grants the consume permissions itself, which is why the role is not given
        // queue actions above.
        Function.AddEventSource(new SqsEventSource(messaging.SourceQueue, new SqsEventSourceProps
        {
            BatchSize = config.BatchSize,
            MaxBatchingWindow = Duration.Seconds(config.BatchWindowSeconds),
            MaxConcurrency = config.MaxConcurrency,

            // The whole point of the batch handler. Without it a single failed record redelivers the
            // other nine, which turns one poison message into a batch that never drains.
            ReportBatchItemFailures = true,

            // Event counts from the poller itself. Without them the only view of the source is the
            // queue's own metrics, which cannot distinguish a poller that has stopped reading from a
            // queue that has stopped filling.
            MetricsConfig = new MetricsConfig { Metrics = [MetricType.EVENT_COUNT] },
        }));
    }

    /// <summary>The function the event source invokes.</summary>
    public IFunction Function { get; }

    /// <summary>
    /// The environment the function reads at cold start.
    /// </summary>
    /// <remarks>
    /// The five required variables, plus the idempotency retention, which is optional to the function
    /// and decided here because it is a per-environment horizon rather than a code default. Every
    /// other optional variable is left unset so the default argued in <c>FunctionConfiguration</c>
    /// applies, rather than being restated here where a second copy would outrank it.
    /// </remarks>
    private static Dictionary<string, string> Variables(
        EnvironmentConfig config,
        PersistenceConstruct persistence) => new(StringComparer.Ordinal)
        {
            ["ORDERS_TABLE_NAME"] = persistence.Orders.TableName,
            ["IDEMPOTENCY_TABLE_NAME"] = persistence.IdempotencyRecords.TableName,
            ["POWERTOOLS_SERVICE_NAME"] = ServiceName,
            ["ENVIRONMENT"] = config.EnvironmentName,
            ["METRICS_NAMESPACE"] = MetricsNamespace,
            ["IDEMPOTENCY_RETENTION_DAYS"] = config.IdempotencyRetentionDays.ToString(CultureInfo.InvariantCulture),
        };
}
