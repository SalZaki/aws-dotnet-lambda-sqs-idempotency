using Amazon.CDK;
using Amazon.CDK.AWS.CloudWatch;
using Amazon.CDK.AWS.CloudWatch.Actions;
using Amazon.CDK.AWS.DynamoDB;
using Amazon.CDK.AWS.SNS;
using Amazon.CDK.AWS.SNS.Subscriptions;
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
    /// The period every metric here is aggregated over, and the unit the multi-period alarms count in.
    /// </summary>
    private const int MetricPeriodMinutes = 5;

    /// <summary>
    /// Prefixes every alarm name, so two environments in one account stay apart.
    /// </summary>
    private readonly string _environmentName;

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

        _environmentName = config.EnvironmentName;

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

        AlarmTopic = new Topic(this, "AlarmTopic", new TopicProps
        {
            TopicName = $"reliable-orders-{config.EnvironmentName}-alarms",
            DisplayName = $"Reliable Orders alarms ({config.EnvironmentName})",
        });

        AlarmTopic.AddSubscription(new EmailSubscription(config.AlarmEndpoint));

        var notify = new SnsAction(AlarmTopic);
        var thresholds = config.AlarmThresholds;

        // Alarm 1. Anything here exhausted its retries, so one message is the threshold.
        Alarm(
            notify,
            "DeadLetterQueueNotEmpty",
            "A message exhausted its retries and is waiting on the dead-letter queue.",
            messaging.DeadLetterQueue.MetricApproximateNumberOfMessagesVisible(Maximum()),
            threshold: 0,
            evaluationPeriods: 1);

        // Alarm 2. A conflict means a key or an order identifier was reused with different data, which
        // is a publisher defect rather than a load condition, so it does not wait for a second sample.
        Alarm(
            notify,
            "IdempotencyConflicts",
            "An idempotency key or order identifier was reused with different data.",
            Custom(IdempotencyConflictsMetric, dimensions),
            threshold: 0,
            evaluationPeriods: 1);

        // Alarm 3.
        Alarm(
            notify,
            "SourceQueueBacklog",
            "The oldest message on the source queue is older than the configured threshold.",
            messaging.SourceQueue.MetricApproximateAgeOfOldestMessage(Maximum()),
            threshold: thresholds.OldestMessageAgeSeconds,
            evaluationPeriods: 1);

        // Alarm 4. Per-minute periods, because the threshold is a number of consecutive minutes.
        Alarm(
            notify,
            "FunctionThrottled",
            "The function was throttled in every one of the last few minutes.",
            processor.Function.MetricThrottles(new MetricOptions
            {
                Statistic = "Sum",
                Period = Duration.Minutes(1),
            }),
            threshold: 0,
            evaluationPeriods: thresholds.ThrottleEvaluationMinutes);

        // Alarm 5.
        Alarm(
            notify,
            "TransientFailures",
            "Records are failing transiently faster than one poison message would explain.",
            Custom(TransientFailuresMetric, dimensions),
            threshold: thresholds.TransientFailuresPerFiveMinutes,
            evaluationPeriods: 1,
            comparison: ComparisonOperator.GREATER_THAN_OR_EQUAL_TO_THRESHOLD);

        // Alarm 6. Throttle events are table-dimensioned; system errors are not, so they come from the
        // per-operation helper. The store issues one operation, so that is the only one named.
        Alarm(
            notify,
            "TableThrottlingOrErrors",
            "DynamoDB throttled a request or returned a system error.",
            new MathExpression(new MathExpressionProps
            {
                Expression = "readThrottles + writeThrottles + systemErrors",
                UsingMetrics = new Dictionary<string, IMetric>(StringComparer.Ordinal)
                {
                    ["readThrottles"] = Throttles(persistence.Orders, ReadThrottleEventsMetric),
                    ["writeThrottles"] = Throttles(persistence.Orders, WriteThrottleEventsMetric),
                    ["systemErrors"] = persistence.Orders.MetricSystemErrorsForOperations(
                        new SystemErrorsForOperationsMetricOptions
                        {
                            Operations = [Operation.TRANSACT_WRITE_ITEMS],
                            Period = Duration.Minutes(5),
                        }),
                },
                Period = Duration.Minutes(5),
            }),
            threshold: 0,
            evaluationPeriods: 1);

        // Alarm 7. Composite, because either half alone is a healthy state: a queue with messages on it
        // is normal, and no processing is normal when nothing has arrived. Only together are they a
        // stall. The sum is what the second half measures — a replay storm leaves OrdersProcessed flat
        // while the service is working, so alarming on new orders alone would fire on correct behaviour.
        var periods = thresholds.NoProgressMinutes / MetricPeriodMinutes;

        var queueHasWork = Alarm(
            notify: null,
            "NoProgressQueueNotEmpty",
            "Messages are available on the source queue.",
            messaging.SourceQueue.MetricApproximateNumberOfMessagesVisible(Maximum()),
            threshold: 0,
            evaluationPeriods: periods);

        var nothingSucceeded = Alarm(
            notify: null,
            "NoProgressNothingProcessed",
            "No record was processed or recognised as a duplicate.",
            new MathExpression(new MathExpressionProps
            {
                Expression = "processed + duplicates",
                UsingMetrics = new Dictionary<string, IMetric>(StringComparer.Ordinal)
                {
                    ["processed"] = Custom(OrdersProcessedMetric, dimensions),
                    ["duplicates"] = Custom(DuplicateEventsMetric, dimensions),
                },
                Period = Duration.Minutes(MetricPeriodMinutes),
            }),
            threshold: 0,
            evaluationPeriods: periods,
            comparison: ComparisonOperator.LESS_THAN_OR_EQUAL_TO_THRESHOLD,

            // Both metrics are published even when zero, so a gap is not "nothing happened" — it is the
            // function not reporting, which is the outage this alarm exists to catch.
            missingData: TreatMissingData.BREACHING);

        var noProgress = new CompositeAlarm(this, "NoProgress", new CompositeAlarmProps
        {
            CompositeAlarmName = $"reliable-orders-{config.EnvironmentName}-no-progress",
            AlarmDescription = "Messages are available and nothing is being processed.",
            AlarmRule = AlarmRule.AllOf(
                AlarmRule.FromAlarm(queueHasWork, AlarmState.ALARM),
                AlarmRule.FromAlarm(nothingSucceeded, AlarmState.ALARM)),
        });

        noProgress.AddAlarmAction(notify);

        // Alarm 8.
        Alarm(
            notify,
            "DeadlineDeferrals",
            "Records were deferred because invocation time was low.",
            Custom(DeadlineDeferralsMetric, dimensions),
            threshold: thresholds.DeadlineDeferralsPerFiveMinutes,
            evaluationPeriods: 1,
            comparison: ComparisonOperator.GREATER_THAN_OR_EQUAL_TO_THRESHOLD);
    }

    /// <summary>The dashboard, exposed so a later construct can add to it rather than build a second.</summary>
    public Dashboard Dashboard { get; }

    /// <summary>
    /// The topic every alarm notifies, exposed so a second subscriber can be added to it.
    /// </summary>
    public Topic AlarmTopic { get; }

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

    /// <summary>
    /// Declares one alarm, and attaches the notification unless it is a leg of a composite.
    /// </summary>
    /// <param name="notify">
    /// The action, or null for a child alarm. A composite's legs are states rather than pages: alarm 7
    /// fires when both hold, and notifying on each would send the two messages it exists to replace.
    /// </param>
    /// <param name="id">The construct identifier, which also suffixes the alarm's name.</param>
    /// <param name="description">What the alarm means, as the notification carries it.</param>
    /// <param name="metric">What is being watched.</param>
    /// <param name="threshold">The value the comparison is made against.</param>
    /// <param name="evaluationPeriods">How many consecutive periods must breach.</param>
    /// <param name="comparison">How the metric is compared to the threshold.</param>
    /// <param name="missingData">What an absent datapoint counts as.</param>
    /// <remarks>
    /// Missing data is not breaching by default. A counter that stayed at zero is omitted rather than
    /// published, so a quiet period arrives here as a gap, and treating that as breaching would alarm
    /// on an idle service. The one alarm that inverts this says so where it is declared.
    /// </remarks>
    private Alarm Alarm(
        SnsAction? notify,
        string id,
        string description,
        IMetric metric,
        double threshold,
        int evaluationPeriods,
        ComparisonOperator comparison = ComparisonOperator.GREATER_THAN_THRESHOLD,
        TreatMissingData missingData = TreatMissingData.NOT_BREACHING)
    {
        var alarm = new Alarm(this, id, new AlarmProps
        {
            AlarmName = $"reliable-orders-{_environmentName}-{id}",
            AlarmDescription = description,
            Metric = metric,
            Threshold = threshold,
            EvaluationPeriods = evaluationPeriods,
            ComparisonOperator = comparison,
            TreatMissingData = missingData,
        });

        if (notify is not null)
        {
            alarm.AddAlarmAction(notify);
        }

        return alarm;
    }

    /// <summary>
    /// The statistic for a queue depth or an age, where the average across a period understates the
    /// backlog an operator is being asked about.
    /// </summary>
    private static MetricOptions Maximum() =>
        new() { Statistic = "Maximum", Period = Duration.Minutes(MetricPeriodMinutes) };

    private static GraphWidget Graph(string title, params IMetric[] metrics) =>
        new(new GraphWidgetProps
        {
            Title = title,
            Left = metrics,
            Width = WidgetWidth,
            Height = WidgetHeight,
        });
}
