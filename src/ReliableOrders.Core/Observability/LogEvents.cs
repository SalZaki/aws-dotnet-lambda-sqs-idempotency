namespace ReliableOrders.Core.Observability;

/// <summary>
/// The identifier of every log event this service emits.
/// </summary>
/// <remarks>
/// <para>
/// Identifiers are stable because operators build saved queries and alarm filters on them. A number
/// is never reused for a different meaning; an event that stops being emitted keeps its number and a
/// new one takes the next free value in its block. Numbers are blocked by concern — batch events in
/// the 1000s, record events in the 2000s — so a new event has an obvious home and the number carries
/// information on its own.
/// </para>
/// <para>
/// Constants rather than <see cref="Microsoft.Extensions.Logging.EventId"/> values, because
/// <c>LoggerMessageAttribute</c> takes the number at compile time. Naming them here rather than
/// inline in each attribute is what makes a duplicate number visible: the whole allocation is one
/// screen, and the attributes read as names.
/// </para>
/// <para>
/// The matching event names are not repeated here. Each attribute in <see cref="ProcessingLog"/>
/// takes its name from the method it decorates, so a rename carries the log name with it and the two
/// cannot disagree.
/// </para>
/// </remarks>
public static class LogEvents
{
    /// <summary>An invocation has begun and its records are known.</summary>
    public const int BatchStarted = 1000;

    /// <summary>An invocation has finished, with the count of records it is failing.</summary>
    public const int BatchCompleted = 1001;

    /// <summary>A message body could not be turned into an order event. Permanent.</summary>
    public const int MessageParsingFailed = 2000;

    /// <summary>A parsed event broke one or more contract rules. Permanent.</summary>
    public const int MessageValidationFailed = 2001;

    /// <summary>An order and its idempotency record were written by this attempt.</summary>
    public const int OrderCreated = 2002;

    /// <summary>The work was already done and the stored data agreed. A success.</summary>
    public const int DuplicateIgnored = 2003;

    /// <summary>
    /// One identity is claimed by two different payloads. Permanent, and alarms.
    /// </summary>
    /// <remarks>
    /// The only event here whose presence at all is a fault in something outside this service. It is
    /// separated from <see cref="MessageValidationFailed"/> because the operator response differs: an
    /// invalid event is one bad message, a conflict means a publisher is emitting contradictory data.
    /// </remarks>
    public const int IdempotencyConflict = 2004;

    /// <summary>The attempt failed for a reason that may not recur. The record is retried.</summary>
    public const int TransientProcessingFailure = 2005;

    /// <summary>
    /// The request was one the store will never accept, which is a fault in this service.
    /// </summary>
    /// <remarks>
    /// Paired with the <c>PermanentFaults</c> metric, and it exists for the same reason. The
    /// nearest event without it is <see cref="TransientProcessingFailure"/>, which would stamp a
    /// retryable outcome on something no retry can fix, leaving the alarm and the log disagreeing
    /// about what happened while an operator waits for a downstream service to recover from a missing
    /// IAM action.
    /// </remarks>
    public const int PermanentProcessingFailure = 2007;

    /// <summary>
    /// A record was not attempted because too little invocation time remained.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="TransientProcessingFailure"/> even though both return the record as a
    /// batch item failure. One is a downstream fault and the other is back-pressure this handler
    /// applied to itself; treating them as one event would leave an operator tuning a queue when the
    /// batch size is what is wrong.
    /// </remarks>
    public const int ProcessingDeadlineReached = 2006;
}
