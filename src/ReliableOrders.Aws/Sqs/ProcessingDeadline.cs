namespace ReliableOrders.Aws.Sqs;

/// <summary>
/// Turns an invocation's remaining time into the instant record processing must stop.
/// </summary>
/// <remarks>
/// <para>
/// The margin is what stops a record being abandoned half-finished when Lambda kills the invocation.
/// It is not free: a deferred record is returned as a batch item failure, so its
/// <c>ApproximateReceiveCount</c> increments on redelivery, and sustained deadline pressure drives
/// valid, never-attempted messages to the dead-letter queue. That is why <c>DeadlineDeferrals</c> is
/// alarmed on, and why the answer to a firing alarm is a smaller batch rather than a smaller margin.
/// </para>
/// <para>
/// <b>The default is provisional.</b> The specification says to size it against observed p99
/// per-record latency, and nothing has run yet, so there is nothing to observe. The value below is a
/// starting point, not a measurement: it allows for one record at a latency dominated by a DynamoDB
/// transaction, plus the runtime's own shutdown. Replace it with a measured p99 once the end-to-end
/// tests in Story 6.3 have produced one, and treat this remark as the reason it must be revisited
/// rather than as a justification for keeping it.
/// </para>
/// </remarks>
public static class ProcessingDeadline
{
    /// <summary>
    /// The provisional safety margin. See the remarks on this type before changing or trusting it.
    /// </summary>
    public static readonly TimeSpan DefaultMargin = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Computes the instant after which no further record is attempted.
    /// </summary>
    /// <remarks>
    /// A remaining time already inside the margin yields a deadline in the past, which defers every
    /// record rather than starting work there is no time to finish. That is the intended reading: the
    /// invocation returns what it has, and SQS redelivers the rest.
    /// </remarks>
    /// <param name="now">The current instant, from the injected clock.</param>
    /// <param name="remaining">What the runtime reports is left of the invocation.</param>
    /// <param name="margin">How much of it to keep back. Defaults to <see cref="DefaultMargin"/>.</param>
    /// <returns>The processing deadline.</returns>
    public static DateTimeOffset From(DateTimeOffset now, TimeSpan remaining, TimeSpan? margin = null) =>
        now + remaining - (margin ?? DefaultMargin);
}
