namespace ReliableOrders.Core.Processing;

/// <summary>
/// One message to process, in terms this project defines rather than the transport's.
/// </summary>
/// <remarks>
/// <para>
/// The reason this type exists is the layering rule. Specification v1 put
/// <c>SQSEvent.SQSMessage</c> on the processor's interface, which would have pulled
/// <c>Amazon.Lambda.SQSEvents</c> into Core and failed the architecture test the same specification
/// asked for. Mapping onto this shape is the composition root's job, and it is the only place that
/// knows a message arrived from SQS.
/// </para>
/// <para>
/// It carries what processing needs and nothing more. Receipt handles, queue URLs and the rest of an
/// SQS record describe how to acknowledge a message, which is the event source mapping's
/// responsibility here, not this service's.
/// </para>
/// </remarks>
/// <param name="MessageId">
/// The transport's identifier for this message. The only identifier that exists before the body is
/// parsed, and the one a batch item failure must report — never the domain event identifier.
/// </param>
/// <param name="Body">
/// The raw body, exactly as delivered. A body that is absent at the transport arrives here as an
/// empty string: the parser reports an empty body and a null body identically, so the mapper has
/// nothing to preserve by keeping them apart.
/// </param>
/// <param name="ApproximateReceiveCount">
/// How many times SQS has delivered this message, counting this delivery. Approximate is the
/// transport's word and worth keeping: it is a redelivery counter, not a guarantee. The
/// first-receipt gate on permanent-failure metrics is built on it, so a value it over-counts costs a
/// data point rather than correctness.
/// </param>
/// <param name="Attributes">
/// Message attributes, carried for trace context propagation. The publisher writes W3C headers here
/// when it supports it, and the event source mapping links producer and consumer traces only because
/// of that — see the Tracing Specification in docs/observability.md.
/// </param>
public sealed record IncomingMessage(
    string MessageId,
    string Body,
    int ApproximateReceiveCount,
    IReadOnlyDictionary<string, string> Attributes)
{
    /// <inheritdoc cref="IncomingMessage(string, string, int, IReadOnlyDictionary{string, string})"/>
    /// <remarks>
    /// Checked here rather than only where SQS records are mapped. The processor promises not to throw
    /// for a message it can classify, and it keeps that promise by opening a log scope from these two
    /// values — which reject a blank identifier and a delivery count below one. Leaving the invariant
    /// in the mapper would hold for records that arrived from SQS and for nothing else: a quarantine
    /// replay, a fixture or a second transport would get an exception out of the processor instead of
    /// a result. The type that promises the shape is the one that enforces it.
    /// </remarks>
    public string MessageId { get; } = Required(MessageId);

    /// <inheritdoc cref="MessageId"/>
    public int ApproximateReceiveCount { get; } = AtLeastFirstDelivery(ApproximateReceiveCount);

    private static string Required(string messageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);

        return messageId;
    }

    private static int AtLeastFirstDelivery(int approximateReceiveCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(approximateReceiveCount, 1);

        return approximateReceiveCount;
    }
}
