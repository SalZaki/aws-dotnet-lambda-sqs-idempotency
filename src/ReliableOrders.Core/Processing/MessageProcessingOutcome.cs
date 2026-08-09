namespace ReliableOrders.Core.Processing;

/// <summary>
/// What processing one message concluded.
/// </summary>
/// <remarks>
/// <para>
/// A flat enum rather than a record hierarchy, which is the one place in this codebase that choice
/// is right. <see cref="Persistence.OrderWriteResult"/> is a hierarchy because each case carries
/// different data and missing one would acknowledge a message that was never stored. These values
/// carry nothing beyond themselves: they are the label a batch handler switches on to decide whether
/// to return the message, and the label a metric groups by. The engineering standards allow an enum
/// for exactly that — flat and dimensionless.
/// </para>
/// <para>
/// The names are the outcome names <c>ProcessingLog</c> writes, so a log line and a result agree
/// without either translating the other.
/// </para>
/// </remarks>
public enum MessageProcessingOutcome
{
    /// <summary>The order and its idempotency record were written by this attempt.</summary>
    Processed,

    /// <summary>
    /// The work was already done and the stored data agreed.
    /// </summary>
    /// <remarks>
    /// A success, and acknowledged like one. Counted apart from <see cref="Processed"/> because a
    /// replay storm is correct behaviour that leaves new orders flat, and the no-progress alarm sums
    /// the two for that reason.
    /// </remarks>
    Duplicate,

    /// <summary>
    /// No retry can succeed — the event is invalid, contradicts stored data, or the request is one
    /// the store will never accept.
    /// </summary>
    /// <remarks>
    /// Still returned as a batch item failure, because this service has no quarantine queue yet and
    /// the dead-letter queue is where a message with no path forward belongs. Story 9.1 is where that
    /// changes.
    /// </remarks>
    PermanentFailure,

    /// <summary>The attempt failed for a reason that may not recur, and the message is redelivered.</summary>
    TransientFailure,

    /// <summary>
    /// The message was returned unattempted because too little invocation time remained.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="TransientFailure"/> because the two mean opposite things to an
    /// operator. One is a downstream fault; this is back-pressure the handler applied to itself, and
    /// sustained deferrals mean the batch size or the deadline margin is wrong rather than that
    /// anything downstream is unwell. The metrics count them separately for the same reason.
    /// </remarks>
    DeadlineDeferred,
}
