namespace ReliableOrders.Core.Observability;

/// <summary>
/// The names every structured log field is written under.
/// </summary>
/// <remarks>
/// <para>
/// A log field's name is an interface. Operators write CloudWatch Logs Insights queries against these
/// strings, alarms are built on those queries, and neither the compiler nor a test in this repository
/// sees a query break when a name is edited. Naming them once here means a rename is a single edit
/// that every writer follows, rather than a search across format strings.
/// </para>
/// <para>
/// These names match the field list in the Logging Specification section of docs/observability.md.
/// Changing one is a change to that document as well.
/// </para>
/// </remarks>
public static class LogFields
{
    /// <summary>
    /// The service emitting the line. Constant for the lifetime of the process.
    /// </summary>
    public const string Service = "Service";

    /// <summary>
    /// The deployment environment. Constant for the lifetime of the process.
    /// </summary>
    public const string Environment = "Environment";

    /// <summary>
    /// The Lambda request identifier, shared by every record in one invocation.
    /// </summary>
    public const string LambdaRequestId = "LambdaRequestId";

    /// <summary>
    /// The SQS message identifier. The only identifier that exists before parsing.
    /// </summary>
    public const string SqsMessageId = "SqsMessageId";

    /// <summary>
    /// The domain event identifier, present once the body has parsed.
    /// </summary>
    /// <remarks>
    /// Named to match the specification, and it collides with the logging framework's own concept of
    /// an event ID — <see cref="Microsoft.Extensions.Logging.EventId"/>, a small integer naming which
    /// log statement was reached. They are unrelated: this one is the publisher's UUID for an order
    /// event. The formatter must therefore keep the framework's value out of this name, and
    /// <see cref="LogEvents"/> gives every log statement a text name so an operator reading a line can
    /// tell which of the two they are looking at.
    /// </remarks>
    public const string EventId = "EventId";

    /// <summary>
    /// The order identifier, present once the body has parsed.
    /// </summary>
    public const string OrderId = "OrderId";

    /// <summary>
    /// The publisher's correlation identifier, when it supplied one.
    /// </summary>
    public const string CorrelationId = "CorrelationId";

    /// <summary>
    /// How many times SQS has delivered this message, counting the current delivery.
    /// </summary>
    /// <remarks>
    /// On the record scope rather than on individual events because it is what separates a first
    /// attempt from a redelivery in every line a record produces, which is the distinction the
    /// permanent-failure metric gate is built on.
    /// </remarks>
    public const string ApproximateReceiveCount = "ApproximateReceiveCount";

    /// <summary>
    /// What processing a record concluded, as a fixed outcome name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Written as a field of the terminal event rather than as scope state. The specification lists it
    /// among the record scope's fields, but a scope is opened before the work it covers and an outcome
    /// exists only after that work has finished; no ordering makes it available to the lines it would
    /// have to precede. Every record reaches exactly one terminal event, so a query that groups by
    /// this field sees each record once either way.
    /// </para>
    /// <para>
    /// No caller supplies the value. Each terminal event on <see cref="ProcessingLog"/> corresponds to
    /// exactly one outcome and writes its own, so the vocabulary cannot drift away from the events
    /// that produce it.
    /// </para>
    /// </remarks>
    public const string Outcome = "Outcome";

    /// <summary>
    /// How long the record took, in milliseconds, on the same terminal event as
    /// <see cref="Outcome"/> and for the same reason.
    /// </summary>
    public const string DurationMs = "DurationMs";

    /// <summary>A low-cardinality reason string, drawn from a fixed vocabulary.</summary>
    /// <remarks>
    /// Never an SDK exception message. Those carry request identifiers, table names and item contents,
    /// which would defeat both grouping and redaction. See
    /// <see cref="Persistence.WriteFailureReason"/>.
    /// </remarks>
    public const string Reason = "Reason";

    /// <summary>
    /// Which idempotency safeguard recognised or refused a write.
    /// </summary>
    public const string Scope = "Scope";

    /// <summary>The hash this event computed to, as hexadecimal.</summary>
    /// <remarks>
    /// A conflict is diagnosed from the hash that was compared, never from the stored item that
    /// disagreed with it. See <see cref="ProcessingLog.IdempotencyConflict"/>.
    /// </remarks>
    public const string ComputedHash = "ComputedHash";

    /// <summary>
    /// The identifiers of the validation rules an event failed.
    /// </summary>
    public const string FailedRules = "FailedRules";

    /// <summary>
    /// How many records the invocation received.
    /// </summary>
    public const string RecordCount = "RecordCount";

    /// <summary>
    /// How many records the invocation is returning as batch item failures.
    /// </summary>
    public const string FailureCount = "FailureCount";

    /// <summary>
    /// Milliseconds of invocation time left when a record was deferred.
    /// </summary>
    public const string RemainingMs = "RemainingMs";
}
