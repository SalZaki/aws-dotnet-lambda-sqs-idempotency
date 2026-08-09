namespace ReliableOrders.Core.Processing;

/// <summary>
/// What happened to one message, and whether the batch response has to mention it.
/// </summary>
/// <remarks>
/// Carries the transport's message identifier rather than the event's. A batch item failure that
/// names an identifier SQS does not recognise makes Lambda reprocess the whole batch, turning one
/// failed record into a ten-record replay, and the domain event identifier is exactly such an
/// identifier. Keeping the right one on the result means the handler has nothing else to reach for.
/// </remarks>
/// <param name="MessageId">The transport identifier this result is about.</param>
/// <param name="Outcome">What processing concluded.</param>
/// <param name="Reason">
/// A low-cardinality label for why this outcome was reached, or null where the outcome has no reason
/// beyond itself. Four vocabularies reach it, one per path: a value from
/// <see cref="Contracts.ParseFailureReason"/> when the body would not parse, a
/// <see cref="Validation.ValidationRule"/> — the first rule broken — when it would not validate, a
/// <see cref="Persistence.DuplicateScope"/> naming which safeguard recognised a duplicate, and a
/// value from <see cref="Persistence.WriteFailureReason"/> for everything the store reports. All four
/// are fixed sets, which is what matters to anything grouping on this. Never an exception message:
/// those vary per call and would defeat both grouping and redaction.
/// </param>
/// <param name="Duration">How long the message took, for the latency the metrics sample.</param>
public sealed record MessageProcessingResult(
    string MessageId,
    MessageProcessingOutcome Outcome,
    string? Reason,
    TimeSpan Duration)
{
    /// <summary>
    /// Whether this message belongs in the batch response's failure list.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Written as "not a success" rather than as a list of the failing outcomes on purpose. A new
    /// outcome added to <see cref="MessageProcessingOutcome"/> then defaults to being retried, which
    /// is the safe direction: a message returned that need not have been is redelivered and resolves
    /// as a duplicate, whereas one acknowledged that should have been retried is gone.
    /// </para>
    /// <para>
    /// A permanent failure is included. Nothing here can succeed on redelivery, but the alternative
    /// is acknowledging a message no one ever sees again; returning it spends the receive attempts
    /// and lands it on the dead-letter queue, where an operator can find it.
    /// </para>
    /// </remarks>
    public bool ShouldReportAsFailure =>
        Outcome is not (MessageProcessingOutcome.Processed or MessageProcessingOutcome.Duplicate);
}
