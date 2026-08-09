namespace ReliableOrders.Core.Observability;

/// <summary>
/// Records what one invocation did, and publishes it when the invocation ends.
/// </summary>
/// <remarks>
/// <para>
/// One method per outcome, in the same shape as <see cref="ProcessingLog"/>, so a caller reports an
/// outcome to both by calling the matching pair. Nothing here takes a metric name or a dimension: the
/// names live in <see cref="MetricNames"/> and the dimensions are fixed by the implementation, which
/// is what keeps a high-cardinality identifier from becoming a dimension by accident.
/// </para>
/// <para>
/// Every method that records a permanent failure requires the delivery number. The gate described in
/// the Retry Amplification of Permanent Failures section is applied once, inside the implementation,
/// rather than at each call site — but a caller cannot record one of these without saying which
/// delivery it was, so the gate can never be skipped by omission. Outcomes that legitimately recur,
/// a transient fault and a deadline deferral, take no delivery number because they are counted every
/// time.
/// </para>
/// <para>
/// Disposing publishes. An invocation that throws still reports what it managed, which matters
/// because the outcomes most worth counting are the ones near a failure.
/// </para>
/// </remarks>
public interface IInvocationMetrics : IDisposable
{
    /// <summary>
    /// A new order was committed.
    /// </summary>
    /// <param name="duration">How long the record took, recorded as latency whatever the outcome.</param>
    void OrderProcessed(TimeSpan duration);

    /// <summary>
    /// The work was already done and the stored data agreed.
    /// </summary>
    /// <remarks>
    /// A success, and counted separately from <see cref="OrderProcessed"/> because a replay storm is
    /// correct behaviour that leaves new orders flat. The no-progress alarm sums the two for exactly
    /// that reason.
    /// </remarks>
    /// <param name="duration">How long the record took.</param>
    void DuplicateEvent(TimeSpan duration);

    /// <summary>
    /// The event will never be acceptable, because it did not parse or broke a contract rule.
    /// </summary>
    /// <remarks>
    /// Published as <see cref="MetricNames.ValidationFailures"/>. Parse and validation failures are
    /// distinct log events and one metric, because the operational response to either is to look at
    /// the publisher.
    /// </remarks>
    /// <param name="approximateReceiveCount">Deliveries so far, counting this one.</param>
    /// <param name="duration">How long the record took.</param>
    void InvalidEvent(int approximateReceiveCount, TimeSpan duration);

    /// <summary>
    /// One identity is claimed by two payloads that disagree.
    /// </summary>
    /// <param name="approximateReceiveCount">Deliveries so far, counting this one.</param>
    /// <param name="duration">How long the record took.</param>
    void IdempotencyConflict(int approximateReceiveCount, TimeSpan duration);

    /// <summary>
    /// The request was one the store will never accept, which is a fault in this service.
    /// </summary>
    /// <remarks>
    /// Gated like the other permanent outcomes, and counted apart from them. See
    /// <see cref="MetricNames.PermanentFaults"/> for why it is not folded into validation failures.
    /// </remarks>
    /// <param name="approximateReceiveCount">Deliveries so far, counting this one.</param>
    /// <param name="duration">How long the record took.</param>
    void PermanentFault(int approximateReceiveCount, TimeSpan duration);

    /// <summary>
    /// The attempt failed for a reason that may not recur.
    /// </summary>
    /// <remarks>
    /// Counted on every delivery, unlike the permanent outcomes. A fault that recurs across
    /// redeliveries is a downstream problem getting worse, and suppressing the repeats would hide the
    /// only signal that says so.
    /// </remarks>
    /// <param name="duration">How long the record took.</param>
    void TransientFailure(TimeSpan duration);

    /// <summary>
    /// A record was returned unattempted because too little invocation time remained.
    /// </summary>
    /// <remarks>
    /// No duration, because no work was done. Counting a deferral's near-zero latency would drag the
    /// latency distribution down exactly when the handler is under the most pressure, which is when
    /// that distribution is being read.
    /// </remarks>
    void DeadlineDeferral();
}
