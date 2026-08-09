namespace ReliableOrders.Core.Observability;

/// <summary>
/// The name of every metric this service publishes.
/// </summary>
/// <remarks>
/// <para>
/// A metric name is harder to change than a log field. Alarms, dashboard widgets and any retained
/// history are all keyed on it, and CloudWatch keeps the old series rather than renaming it, so a
/// changed name silently splits a metric in two and leaves every alarm watching the half that stopped
/// moving. Naming them once here means the writer, the tests and the CDK dashboard cannot disagree.
/// </para>
/// <para>
/// These match the metrics table in the Metrics Specification section of docs/observability.md.
/// </para>
/// </remarks>
public static class MetricNames
{
    /// <summary>New orders committed. Unit: Count.</summary>
    public const string OrdersProcessed = "OrdersProcessed";

    /// <summary>Duplicate events safely ignored. Unit: Count.</summary>
    public const string DuplicateEvents = "DuplicateEvents";

    /// <summary>
    /// Permanently invalid events. Unit: Count.
    /// </summary>
    /// <remarks>
    /// Covers both a body that would not parse and one that parsed but broke a contract rule. The two
    /// are separate log events because an operator diagnosing one message wants to know which, and one
    /// metric because the operational response to either is the same: a publisher is sending events
    /// this service cannot accept.
    /// </remarks>
    public const string ValidationFailures = "ValidationFailures";

    /// <summary>An identity reused with different data. Unit: Count.</summary>
    public const string IdempotencyConflicts = "IdempotencyConflicts";

    /// <summary>
    /// Permanent faults in this service rather than in the event. Unit: Count.
    /// </summary>
    /// <remarks>
    /// Not in the specification's table, and added because without it a
    /// <c>permanent.table-not-found</c> or <c>permanent.access-denied</c> produces no metric at all —
    /// the message exhausts its retries and dead-letters with nothing to alarm on. Folding these into
    /// <see cref="ValidationFailures"/> was the alternative and is worse than the gap: it would point
    /// the conflict and validation runbooks at a publisher when the cause is a missing IAM action or a
    /// wrong table name in an environment variable. See <c>WriteFailureReason</c>, which draws the
    /// same line for the same reason.
    /// </remarks>
    public const string PermanentFaults = "PermanentFaults";

    /// <summary>Retryable record failures. Unit: Count.</summary>
    public const string TransientFailures = "TransientFailures";

    /// <summary>End-to-end per-record processing duration. Unit: Milliseconds.</summary>
    public const string RecordProcessingLatency = "RecordProcessingLatency";

    /// <summary>Records received by the invocation. Unit: Count.</summary>
    public const string BatchSize = "BatchSize";

    /// <summary>
    /// Records returned in the batch response as failures. Unit: Count.
    /// </summary>
    /// <remarks>
    /// The metric that makes a partial failure visible. Lambda reports an invocation returning
    /// <c>BatchItemFailures</c> as successful, so its own error and duration metrics stay flat while
    /// records are being retried; without this one, nothing outside the logs says so.
    /// </remarks>
    public const string BatchFailures = "BatchFailures";

    /// <summary>Records deferred because invocation time was low. Unit: Count.</summary>
    public const string DeadlineDeferrals = "DeadlineDeferrals";
}
