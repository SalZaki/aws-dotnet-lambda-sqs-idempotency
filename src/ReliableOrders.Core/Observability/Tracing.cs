using System.Diagnostics;
using System.Reflection;

namespace ReliableOrders.Core.Observability;

/// <summary>
/// The one <see cref="ActivitySource"/> this service starts spans from, and the names it starts them
/// under.
/// </summary>
/// <remarks>
/// <para>
/// One source, not one per component. A source is what a subscriber registers against, so a second
/// one is a second registration to remember: the composition root would have to name it, a test
/// listener would have to subscribe to it, and the failure mode of forgetting is missing spans rather
/// than an error. Nothing is gained in return, because the span name already says which step ran.
/// </para>
/// <para>
/// Static, and deliberately so, although nothing else in this service is. An
/// <see cref="ActivitySource"/> is documented as safe to hold for the process lifetime and injecting
/// one would put a parameter on every constructor that traces, for a dependency no test needs to
/// substitute — <see cref="ActivityListener"/> subscribes to the real source, so a test observes real
/// spans rather than a fake's record of them.
/// </para>
/// <para>
/// When nothing is listening, <see cref="ActivitySource.StartActivity(string, ActivityKind)"/> returns
/// null and costs almost nothing. Every call site therefore treats a null activity as ordinary rather
/// than as a failure, which is also what happens in production for any span a sampler drops.
/// </para>
/// </remarks>
public static class Tracing
{
    /// <summary>
    /// The source name. Registered by the composition root and by any test that listens.
    /// </summary>
    /// <remarks>
    /// The assembly name rather than a literal, so the string a subscriber registers cannot drift from
    /// the assembly it describes. It is also what appears as the instrumentation scope on every
    /// exported span.
    /// </remarks>
    public static readonly string SourceName = typeof(Tracing).Assembly.GetName().Name!;

    /// <summary>
    /// The version reported alongside the source, taken from the assembly.
    /// </summary>
    public static readonly string SourceVersion =
        typeof(Tracing).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
        ?? typeof(Tracing).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";

    /// <summary>
    /// The source itself.
    /// </summary>
    public static readonly ActivitySource Source = new(SourceName, SourceVersion);

    /// <summary>
    /// The names each span is started under.
    /// </summary>
    /// <remarks>
    /// A span name is an interface in the same way a log field is. Operators group by it and build
    /// service-map views on it, so it is named once here rather than spelled at each call site. The
    /// vocabulary is fixed and low cardinality: no identifier is ever part of a name, because a name
    /// carrying an order identifier produces one distinct operation per order and makes every
    /// aggregate view useless.
    /// </remarks>
    public static class Spans
    {
        /// <summary>One record, from receipt to outcome.</summary>
        public const string ProcessRecord = "order.process";

        /// <summary>Reading a body into a typed event.</summary>
        public const string Parse = "order.parse";

        /// <summary>Checking the event against the contract rules.</summary>
        public const string Validate = "order.validate";

        /// <summary>Computing the two canonical hashes.</summary>
        public const string Hash = "order.hash";

        /// <summary>The transactional write.</summary>
        public const string Persist = "order.persist";

        /// <summary>Turning a cancelled transaction into a duplicate or a conflict.</summary>
        public const string Classify = "order.classify";
    }

    /// <summary>
    /// The names span attributes are written under.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two vocabularies, on purpose. Where OpenTelemetry defines a convention the convention wins,
    /// because the value of <c>messaging.*</c> is that a backend already knows what it means and will
    /// render a consumer span as one. Everything with no convention is prefixed
    /// <c>reliable_orders.</c> so it is visibly this service's own and cannot collide with a future
    /// convention of the same name.
    /// </para>
    /// <para>
    /// What is absent matters as much. No body, no customer identifier, no amount, no item
    /// description. Traces are sampled and exported to a different system than logs, under different
    /// retention, and the Do Not Log list in docs/security.md does not stop applying because the
    /// destination changed. The identifiers below are the same three the log scope already carries.
    /// </para>
    /// </remarks>
    public static class Attributes
    {
        /// <summary>The transport, always <c>aws_sqs</c>. An OpenTelemetry convention.</summary>
        public const string MessagingSystem = "messaging.system";

        /// <summary>
        /// The Lambda request identifier. An OpenTelemetry convention.
        /// </summary>
        /// <remarks>
        /// On every record span, which is what makes the records of one invocation findable together.
        /// It is carried as an attribute rather than by giving the batch a span of its own to be
        /// children of: a batch holds up to ten independent records, each with whatever trace context
        /// its own publisher wrote, so a batch span would be a parent joining unrelated traces.
        /// </remarks>
        public const string FaasInvocationId = "faas.invocation_id";

        /// <summary>The SQS message identifier. An OpenTelemetry convention.</summary>
        public const string MessagingMessageId = "messaging.message.id";

        /// <summary>
        /// What the consumer did with the message. An OpenTelemetry convention.
        /// </summary>
        /// <remarks>
        /// <c>messaging.operation.type</c>, not the <c>messaging.operation</c> it replaced. The point
        /// of preferring a convention is that a backend already knows how to render it, and a retired
        /// name buys none of that — it arrives as an attribute nothing recognises.
        /// </remarks>
        public const string MessagingOperation = "messaging.operation.type";

        /// <summary>How many times SQS has delivered this message, counting this one.</summary>
        public const string ReceiveCount = "reliable_orders.receive_count";

        /// <summary>The domain event identifier, once the body has parsed.</summary>
        public const string EventId = "reliable_orders.event_id";

        /// <summary>The order identifier, once the body has parsed.</summary>
        public const string OrderId = "reliable_orders.order_id";

        /// <summary>The publisher's correlation identifier, when it supplied one.</summary>
        public const string CorrelationId = "reliable_orders.correlation_id";

        /// <summary>The outcome the record reached, from the same vocabulary the logs use.</summary>
        public const string Outcome = "reliable_orders.outcome";

        /// <summary>A low-cardinality reason, from the same fixed vocabulary the logs use.</summary>
        public const string Reason = "reliable_orders.reason";

        /// <summary>Which idempotency safeguard recognised or refused a write.</summary>
        public const string Scope = "reliable_orders.scope";
    }

    /// <summary>
    /// The value <see cref="Attributes.MessagingSystem"/> always carries.
    /// </summary>
    public const string MessagingSystemValue = "aws_sqs";

    /// <summary>
    /// The value <see cref="Attributes.MessagingOperation"/> always carries.
    /// </summary>
    /// <remarks>
    /// <c>process</c> rather than <c>receive</c>. The event source mapping does the receiving; this
    /// service is handed records that were already taken off the queue, and the distinction is what
    /// tells a reader the span does not cover the poll.
    /// </remarks>
    public const string MessagingOperationValue = "process";
}
