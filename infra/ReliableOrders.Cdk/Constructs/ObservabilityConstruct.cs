using Amazon.CDK;
using Amazon.CDK.AWS.CloudWatch;
using Amazon.CDK.AWS.DynamoDB;
using Constructs;
using ReliableOrders.Cdk.Configuration;

namespace ReliableOrders.Cdk.Constructs;

/// <summary>
/// The dashboard an operator opens when something looks wrong.
/// </summary>
/// <remarks>
/// <para>
/// The widget list is the one in docs/observability.md, one widget per entry rather than several
/// series folded together. Folding puts series of different magnitudes on one axis, where the small
/// one is unreadable.
/// </para>
/// <para>
/// This is the only construct that depends on all three of the others, because every widget names a
/// queue, a table or the function. It is built last in the stack for that reason.
/// </para>
/// <para>
/// The custom metric names below are duplicated from <c>ReliableOrders.Core.Observability.MetricNames</c>
/// rather than referenced, so that synthesising infrastructure does not require the application
/// assembly. The duplication is pinned by a test in ReliableOrders.CdkTests, which already references
/// both projects. Left unpinned, a renamed metric would leave a widget querying a name nothing emits,
/// and an empty widget reads as an outage rather than as a mistake.
/// </para>
/// </remarks>
public sealed class ObservabilityConstruct : Construct
{
    /// <summary>New orders committed.</summary>
    public const string OrdersProcessedMetric = "OrdersProcessed";

    /// <summary>Duplicate events safely ignored.</summary>
    public const string DuplicateEventsMetric = "DuplicateEvents";

    /// <summary>Key or order identifier reused with different data.</summary>
    public const string IdempotencyConflictsMetric = "IdempotencyConflicts";

    /// <summary>Permanently invalid events.</summary>
    public const string ValidationFailuresMetric = "ValidationFailures";

    /// <summary>Requests the store will never accept.</summary>
    public const string PermanentFaultsMetric = "PermanentFaults";

    /// <summary>Retryable record failures.</summary>
    public const string TransientFailuresMetric = "TransientFailures";

    /// <summary>End-to-end per-record processing duration.</summary>
    public const string RecordProcessingLatencyMetric = "RecordProcessingLatency";

    /// <summary>Records deferred because invocation time was low.</summary>
    public const string DeadlineDeferralsMetric = "DeadlineDeferrals";

    /// <summary>Read requests DynamoDB throttled, published against the table.</summary>
    public const string ReadThrottleEventsMetric = "ReadThrottleEvents";

    /// <summary>Write requests DynamoDB throttled, published against the table.</summary>
    public const string WriteThrottleEventsMetric = "WriteThrottleEvents";

    /// <summary>The dimension naming the service that published a metric.</summary>
    public const string ServiceDimension = "Service";

    /// <summary>The dimension naming the deployment that published it.</summary>
    public const string EnvironmentDimension = "Environment";

    private const int WidgetWidth = 6;
    private const int WidgetHeight = 6;

    /// <summary>
    /// Creates the dashboard over the queues, the tables and the function.
    /// </summary>
    /// <param name="scope">The stack these widgets belong to.</param>
    /// <param name="id">The construct identifier, which prefixes the dashboard's logical ID.</param>
    /// <param name="config">Names the deployment, which dimensions every custom metric.</param>
    /// <param name="messaging">The source queue and the dead-letter queue.</param>
    /// <param name="persistence">The orders and idempotency tables.</param>
    /// <param name="processor">The function that consumes the queue.</param>
    public ObservabilityConstruct(
        Construct scope,
        string id,
        EnvironmentConfig config,
        MessagingConstruct messaging,
        PersistenceConstruct persistence,
        OrderProcessorConstruct processor)
        : base(scope, id)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(messaging);
        ArgumentNullException.ThrowIfNull(persistence);
        ArgumentNullException.ThrowIfNull(processor);

        var dimensions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ServiceDimension] = OrderProcessorConstruct.ServiceName,
            [EnvironmentDimension] = config.EnvironmentName,
        };

        Dashboard = new Dashboard(this, "OrdersDashboard", new DashboardProps
        {
            DashboardName = $"reliable-orders-{config.EnvironmentName}",
        });

        Dashboard.AddWidgets(
            Graph("SQS visible messages", messaging.SourceQueue.MetricApproximateNumberOfMessagesVisible()),
            Graph("SQS messages in flight", messaging.SourceQueue.MetricApproximateNumberOfMessagesNotVisible()),
            Graph("Age of oldest source message", messaging.SourceQueue.MetricApproximateAgeOfOldestMessage()),
            Graph("DLQ visible messages", messaging.DeadLetterQueue.MetricApproximateNumberOfMessagesVisible()));

        Dashboard.AddWidgets(
            Graph("Lambda invocations", processor.Function.MetricInvocations()),
            Graph("Lambda errors", processor.Function.MetricErrors()),
            Graph("Lambda throttles", processor.Function.MetricThrottles()),
            Graph("Lambda duration", processor.Function.MetricDuration()));

        Dashboard.AddWidgets(
            Graph(
                "Lambda concurrent executions",
                processor.Function.Metric("ConcurrentExecutions", new MetricOptions { Statistic = "Maximum" })),
            Graph(
                "DynamoDB consumed capacity",
                persistence.Orders.MetricConsumedWriteCapacityUnits(),
                persistence.Orders.MetricConsumedReadCapacityUnits(),
                persistence.IdempotencyRecords.MetricConsumedWriteCapacityUnits(),
                persistence.IdempotencyRecords.MetricConsumedReadCapacityUnits()),
            // Throttle events rather than ThrottledRequests. DynamoDB publishes ThrottledRequests only
            // against (TableName, Operation), so a table-dimensioned query for it matches no series and
            // draws a flat line, which reads as "not throttling" while the table is. ReadThrottleEvents
            // and WriteThrottleEvents are published against the table.
            Graph(
                "DynamoDB throttled requests",
                Throttles(persistence.Orders, ReadThrottleEventsMetric),
                Throttles(persistence.Orders, WriteThrottleEventsMetric),
                Throttles(persistence.IdempotencyRecords, ReadThrottleEventsMetric),
                Throttles(persistence.IdempotencyRecords, WriteThrottleEventsMetric)),

            // The four outcomes on one axis, because the question an operator asks here is which of
            // them moved rather than how many of one there were.
            Graph(
                "Processed, duplicate, conflict and failure",
                Custom(OrdersProcessedMetric, dimensions),
                Custom(DuplicateEventsMetric, dimensions),
                Custom(IdempotencyConflictsMetric, dimensions),
                Custom(ValidationFailuresMetric, dimensions),
                Custom(PermanentFaultsMetric, dimensions),
                Custom(TransientFailuresMetric, dimensions)));

        Dashboard.AddWidgets(
            Graph(
                "Per-record latency",
                Custom(RecordProcessingLatencyMetric, dimensions, "p50"),
                Custom(RecordProcessingLatencyMetric, dimensions, "p99")),
            Graph("Deadline deferrals", Custom(DeadlineDeferralsMetric, dimensions)));
    }

    /// <summary>The dashboard, exposed so a later construct can add to it rather than build a second.</summary>
    public Dashboard Dashboard { get; }

    /// <summary>
    /// A metric this service publishes, dimensioned the way the publisher dimensions it.
    /// </summary>
    /// <remarks>
    /// Summed over five minutes, matching the windows the alarm thresholds are written in. A statistic
    /// is supplied only for the latency metric, where a sum means nothing.
    /// </remarks>
    private static Metric Custom(
        string metricName,
        IDictionary<string, string> dimensions,
        string statistic = "Sum") =>
        new(new MetricProps
        {
            Namespace = OrderProcessorConstruct.MetricsNamespace,
            MetricName = metricName,
            DimensionsMap = dimensions,
            Statistic = statistic,
            Period = Duration.Minutes(5),
        });

    /// <summary>
    /// A table-level throttle metric, summed over the same window as the custom metrics.
    /// </summary>
    private static Metric Throttles(ITable table, string metricName) =>
        table.Metric(metricName, new MetricOptions
        {
            Statistic = "Sum",
            Period = Duration.Minutes(5),
        });

    private static GraphWidget Graph(string title, params IMetric[] metrics) =>
        new(new GraphWidgetProps
        {
            Title = title,
            Left = metrics,
            Width = WidgetWidth,
            Height = WidgetHeight,
        });
}
