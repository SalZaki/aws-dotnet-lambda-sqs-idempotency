using System.Diagnostics;
using ReliableOrders.Core.Observability;
using ReliableOrders.Core.Processing;

namespace ReliableOrders.Aws.Sqs;

/// <summary>
/// Starts the span one record is processed under, continuing the publisher's trace when it left one.
/// </summary>
/// <remarks>
/// <para>
/// The event source mapping does not link producer and consumer traces. Nothing about SQS carries
/// trace context on its own: the link exists only because a publisher wrote W3C headers into the
/// message attributes and this reads them back. A message from a publisher that writes none is not an
/// error and is not a broken trace — it is a record whose span is a root, which is what an
/// unpropagated context looks like everywhere.
/// </para>
/// <para>
/// Per record rather than per batch. Ten records can carry ten different trace contexts, so a span
/// covering the invocation would have to pick one publisher's trace to belong to and misattribute the
/// other nine. What ties an invocation's records together instead is
/// <see cref="Tracing.Attributes.FaasInvocationId"/>, which every record span carries.
/// </para>
/// </remarks>
public static class RecordTrace
{
    /// <summary>
    /// The message attribute a publisher writes W3C trace context into.
    /// </summary>
    /// <remarks>
    /// Lower case, as the W3C specification defines it. The lookup is case-insensitive anyway —
    /// <see cref="IncomingMessageMapper"/> builds the dictionary that way, because SQS preserves
    /// whatever casing the publisher used and a publisher writing <c>TraceParent</c> would otherwise
    /// be invisible to a consumer reading <c>traceparent</c>.
    /// </remarks>
    public const string TraceParentAttribute = "traceparent";

    /// <inheritdoc cref="TraceParentAttribute"/>
    public const string TraceStateAttribute = "tracestate";

    /// <summary>
    /// Starts the record's span.
    /// </summary>
    /// <param name="message">The record, for its identifiers and the publisher's context.</param>
    /// <param name="lambdaRequestId">The invocation every record of this batch shares.</param>
    /// <returns>
    /// The span, or null when nothing is listening. A null is ordinary: it is what an unsampled record
    /// produces in production too, so every caller treats it as a normal outcome rather than a fault.
    /// </returns>
    public static Activity? Start(IncomingMessage message, string lambdaRequestId)
    {
        ArgumentNullException.ThrowIfNull(message);

        var parent = ParentOf(message);

        // Consumer, which is what tells a backend to render this as the receiving half of a
        // producer-consumer pair rather than as an ordinary internal step.
        var span = Tracing.Source.StartActivity(
            Tracing.Spans.ProcessRecord,
            ActivityKind.Consumer,
            parent);

        if (span is null)
        {
            return null;
        }

        span.SetTag(Tracing.Attributes.MessagingSystem, Tracing.MessagingSystemValue);
        span.SetTag(Tracing.Attributes.MessagingOperation, Tracing.MessagingOperationValue);
        span.SetTag(Tracing.Attributes.MessagingMessageId, message.MessageId);
        span.SetTag(Tracing.Attributes.ReceiveCount, message.ApproximateReceiveCount);
        span.SetTag(Tracing.Attributes.FaasInvocationId, lambdaRequestId);

        return span;
    }

    /// <summary>
    /// The publisher's context, or the default when there is none to read.
    /// </summary>
    /// <remarks>
    /// A malformed header is treated as an absent one. The alternative is failing a record over a
    /// diagnostic field, and a publisher that writes a broken <c>traceparent</c> is a publisher whose
    /// orders still have to be processed. <see cref="ActivityContext.TryParse(string, string, out
    /// ActivityContext)"/> is what decides, so the definition of malformed is the W3C specification's
    /// rather than this service's.
    /// </remarks>
    private static ActivityContext ParentOf(IncomingMessage message)
    {
        if (!message.Attributes.TryGetValue(TraceParentAttribute, out var traceParent))
        {
            return default;
        }

        message.Attributes.TryGetValue(TraceStateAttribute, out var traceState);

        // Remote, which the three-argument overload would leave false. The flag is what says the
        // context arrived from another process, and consumers act on it — the X-Ray translator reads
        // it to decide whether this is the service's entry point or a subsegment of a span that never
        // ran here.
        return ActivityContext.TryParse(traceParent, traceState, isRemote: true, out var parent)
            ? parent
            : default;
    }
}
