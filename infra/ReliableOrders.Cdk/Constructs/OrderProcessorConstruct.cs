using System.Globalization;
using Amazon.CDK;
using Amazon.CDK.AWS.IAM;
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

            // Stated rather than left to CDK's x86_64 default, because it is half of a pair. The
            // collector layer below is published per architecture, so this value and that ARN have to
            // agree — and a disagreement fails when the extension initialises in the deployed
            // environment, long after a synthesis that succeeded. A CDK assertion holds the pair
            // together; changing this alone fails it, naming the layer.
            Architecture = ProcessorArchitecture,

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

            // The collector the function exports OTLP to. It runs in the execution environment beside
            // the function and forwards to X-Ray, which is what keeps the export off the record path:
            // the SDK hands spans to a local process rather than calling an AWS API inside the
            // invocation.
            Layers = [CollectorLayer(this)],
        });

        persistence.GrantOrderTransaction(Function.Role!);

        // The collector writes the traces, so the function's role is what needs the permission. Both
        // actions are unscoped because X-Ray defines no resource for them — the API takes segments,
        // not a resource ARN — so a resource-scoped statement is not available to write. That is the
        // one exception to the resource-scoping rule in docs/security.md, and it is recorded there.
        Function.AddToRolePolicy(new PolicyStatement(new PolicyStatementProps
        {
            Actions = ["xray:PutTraceSegments", "xray:PutTelemetryRecords"],
            Resources = ["*"],
        }));

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
    /// The architecture the function deploys as, which the collector layer is chosen to match.
    /// </summary>
    /// <remarks>
    /// Named once and used twice — on the function and in the layer's name — so the two cannot drift
    /// silently. It is not derived from the layer string or the other way round: the arm64 collector is
    /// a separate publication and switching architecture is a deliberate change to both, not a
    /// substitution one place can make on the other's behalf.
    /// </remarks>
    private static readonly Architecture ProcessorArchitecture = Architecture.X86_64;

    /// <summary>
    /// The AWS account that publishes the ADOT Lambda layers.
    /// </summary>
    /// <remarks>
    /// AWS's own, and the same in every commercial Region. The layer is a regional resource, so the
    /// ARN is composed against the stack's Region rather than written out.
    /// </remarks>
    private const string AdotPublisherAccount = "901920570463";

    /// <summary>
    /// The collector layer, pinned to a version.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The collector alone, not one of the language layers. Those exist to auto-instrument, and the
    /// observability specification is explicit that auto-instrumentation on Lambda is far weaker for
    /// .NET than for other runtimes — this service therefore instruments itself and needs only
    /// somewhere to send the result. The corollary is that <c>AWS_LAMBDA_EXEC_WRAPPER</c> is
    /// deliberately not set: it points at the auto-instrumentation entry script, which this function
    /// must not run.
    /// </para>
    /// <para>
    /// <c>amd64</c>, which is the layer published for the x86_64 in
    /// <see cref="ProcessorArchitecture"/>. There is an arm64 layer, and picking the wrong one fails
    /// when the extension initialises rather than at synthesis — so the two move together or not at
    /// all, and a CDK assertion over the pair is what makes that more than an instruction.
    /// </para>
    /// <para>
    /// Pinned to a version for the reason the container images are: an unpinned layer changes what
    /// runs beside the function without a deployment. Unlike an image digest, nothing here can verify
    /// the ARN exists — a wrong version is rejected at deploy, not at synthesis.
    /// </para>
    /// </remarks>
    private const string AdotCollectorLayer = "aws-otel-collector-amd64-ver-0-151-0:1";

    /// <remarks>
    /// Scoped to the construct rather than to its parent, so the layer is a child of the thing that
    /// uses it. Under the parent, a second processor construct in one stack collides on the logical
    /// identifier at synthesis, and this construct leaks a child into a scope it does not own.
    /// </remarks>
    private static ILayerVersion CollectorLayer(Construct scope)
    {
        var stack = Stack.Of(scope);

        return LayerVersion.FromLayerVersionArn(
            scope,
            "AdotCollectorLayer",
            $"arn:{stack.Partition}:lambda:{stack.Region}:{AdotPublisherAccount}:layer:{AdotCollectorLayer}");
    }

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
