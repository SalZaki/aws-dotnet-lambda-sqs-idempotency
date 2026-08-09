using System.Globalization;
using Amazon.Lambda.SQSEvents;
using ReliableOrders.Core.Processing;

namespace ReliableOrders.Function.Sqs;

/// <summary>
/// Turns an SQS record into the shape this service processes.
/// </summary>
/// <remarks>
/// <para>
/// The one place that knows a message came from SQS. Keeping it here rather than in
/// <c>ReliableOrders.Core</c> is what the layering rule is for: Core defines
/// <see cref="IncomingMessage"/> so that <c>Amazon.Lambda.SQSEvents</c> never reaches it, and an
/// architecture test fails the build if it ever does.
/// </para>
/// <para>
/// Every value it reads is one SQS may omit. The body and the receive count have a stated fallback;
/// the message identifier cannot have one, because it is what a batch item failure has to name, so
/// its absence is rejected here rather than left to surface as a null several layers away.
/// </para>
/// </remarks>
public static class IncomingMessageMapper
{
    /// <summary>
    /// The attribute SQS delivers the redelivery counter under.
    /// </summary>
    public const string ApproximateReceiveCountAttribute = "ApproximateReceiveCount";

    /// <summary>
    /// Maps one record.
    /// </summary>
    /// <param name="record">The record as the runtime deserialised it.</param>
    /// <returns>The message to process.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="record"/> is null.</exception>
    public static IncomingMessage ToIncomingMessage(this SQSEvent.SQSMessage record)
    {
        ArgumentNullException.ThrowIfNull(record);

        // The message identifier has no fallback and is not checked here: IncomingMessage rejects a
        // blank one itself, so every producer of one gets the same answer rather than only this
        // mapper's callers.
        return new IncomingMessage(
            record.MessageId,
            // An absent body and an empty one are the same thing to the parser, which reports
            // body.empty for both, so coalescing loses nothing a caller could have acted on.
            record.Body ?? string.Empty,
            ReadReceiveCount(record),
            ReadAttributes(record));
    }

    /// <summary>
    /// Reads the redelivery counter, defaulting to a first delivery.
    /// </summary>
    /// <remarks>
    /// The value arrives as a string in a dictionary SQS populates, so it can be absent or
    /// unparseable in a way the type system does not prevent — a locally constructed record, a
    /// changed transport, a test fixture. Defaulting to one is the deliberate direction: the
    /// first-receipt gate publishes a permanent-failure metric when the count is one, so an
    /// unreadable counter produces a data point rather than swallowing one. An over-count is a
    /// duplicate point on a dashboard; an under-count is a poison message no alarm ever mentions.
    /// </remarks>
    private static int ReadReceiveCount(SQSEvent.SQSMessage record)
    {
        if (record.Attributes is null
            || !record.Attributes.TryGetValue(ApproximateReceiveCountAttribute, out var raw)
            || !int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var count)
            || count < 1)
        {
            return 1;
        }

        return count;
    }

    /// <summary>
    /// Reads the message attributes, as string values only.
    /// </summary>
    /// <remarks>
    /// Carried for trace context, which the publisher writes as W3C headers. Binary attribute values
    /// are dropped rather than encoded: nothing reads them, and a base64 blob on every message would
    /// be paid for on a path where log and metric volume is already the dominant cost.
    /// </remarks>
    private static Dictionary<string, string> ReadAttributes(SQSEvent.SQSMessage record)
    {
        if (record.MessageAttributes is not { Count: > 0 })
        {
            return [];
        }

        // Case-insensitive, because the only documented reader is trace context and W3C header names
        // are case-insensitive while SQS preserves whatever casing the publisher used. A publisher
        // writing TraceParent would otherwise be invisible to a consumer reading traceparent, and the
        // symptom is producer and consumer traces that silently fail to link. Two attributes differing
        // only in case collapse to the last one seen, which is the lesser problem of the two.
        var attributes = new Dictionary<string, string>(
            record.MessageAttributes.Count,
            StringComparer.OrdinalIgnoreCase);

        foreach (var (name, value) in record.MessageAttributes)
        {
            if (value?.StringValue is { } text)
            {
                attributes[name] = text;
            }
        }

        return attributes;
    }
}
