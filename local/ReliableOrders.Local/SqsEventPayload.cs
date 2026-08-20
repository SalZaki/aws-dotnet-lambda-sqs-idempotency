using System.Text.Json.Serialization;

namespace ReliableOrders.Local;

/// <summary>
/// The batch as the Lambda runtime would hand it to the function, and the response it returns.
/// </summary>
/// <remarks>
/// <para>
/// Written out here rather than borrowed from <c>Amazon.Lambda.SQSEvents</c>. Those types are what
/// the function deserialises <i>into</i>, and the serializer it uses matches property names without
/// regard to case — so reusing them would let this program send a shape the real event source
/// mapping never sends, and the mismatch would be invisible for as long as the case-insensitive
/// match kept covering it. The names below are the wire contract, and every one is stated.
/// </para>
/// <para>
/// <c>Records</c> is capitalised and everything inside it is not. That is not a mistake in either
/// direction: it is what AWS sends, and <c>LambdaSerializerTests</c> pins the same shape from the
/// other side.
/// </para>
/// </remarks>
internal sealed class SqsEventPayload
{
    /// <summary>The records this invocation is given.</summary>
    [JsonPropertyName("Records")]
    public required IReadOnlyList<SqsRecordPayload> Records { get; init; }
}

/// <summary>
/// One record of a batch.
/// </summary>
/// <remarks>
/// Only the fields something downstream reads are carried. <c>ApproximateReceiveCount</c> is the one
/// that changes behaviour — the first-receipt metric gate reads it — and the message attributes are
/// carried because trace context travels in them. Adding the rest would suggest the mapper depends
/// on them.
/// </remarks>
internal sealed class SqsRecordPayload
{
    /// <summary>What a batch item failure names, and what SQS matches a response against.</summary>
    [JsonPropertyName("messageId")]
    public required string MessageId { get; init; }

    /// <summary>What a delete or a visibility change is made against.</summary>
    [JsonPropertyName("receiptHandle")]
    public required string ReceiptHandle { get; init; }

    /// <summary>The order event, as published.</summary>
    [JsonPropertyName("body")]
    public required string Body { get; init; }

    /// <summary>The system attributes, of which only the receive count is read.</summary>
    [JsonPropertyName("attributes")]
    public required IReadOnlyDictionary<string, string> Attributes { get; init; }

    /// <summary>The publisher's own attributes, which carry trace context.</summary>
    [JsonPropertyName("messageAttributes")]
    public required IReadOnlyDictionary<string, SqsMessageAttributePayload> MessageAttributes { get; init; }

    /// <summary>Always <c>aws:sqs</c>, as it is on a real record.</summary>
    [JsonPropertyName("eventSource")]
    public required string EventSource { get; init; }

    /// <summary>The queue the record came from.</summary>
    [JsonPropertyName("eventSourceARN")]
    public required string EventSourceArn { get; init; }

    /// <summary>The region the queue is in.</summary>
    [JsonPropertyName("awsRegion")]
    public required string AwsRegion { get; init; }
}

/// <summary>
/// One message attribute, in the shape the runtime delivers it.
/// </summary>
internal sealed class SqsMessageAttributePayload
{
    /// <summary>The value, for the string types this service publishes.</summary>
    [JsonPropertyName("stringValue")]
    public string? StringValue { get; init; }

    /// <summary>The declared type, which SQS requires and nothing here branches on.</summary>
    [JsonPropertyName("dataType")]
    public required string DataType { get; init; }
}

/// <summary>
/// The partial batch response the function returns.
/// </summary>
/// <remarks>
/// The list is nullable because an absent one is meaningful. Lambda reads a response with no
/// <c>batchItemFailures</c> key as a batch that wholly succeeded, and this program has to read it the
/// same way — a stand-in that treated the absence as an error would report a failure the real mapping
/// does not.
/// </remarks>
internal sealed class SqsBatchResponsePayload
{
    /// <summary>The records to redeliver.</summary>
    [JsonPropertyName("batchItemFailures")]
    public IReadOnlyList<BatchItemFailurePayload>? BatchItemFailures { get; init; }
}

/// <summary>
/// One record the function asked to have redelivered.
/// </summary>
internal sealed class BatchItemFailurePayload
{
    /// <summary>The SQS message identifier, never a domain event identifier.</summary>
    [JsonPropertyName("itemIdentifier")]
    public string? ItemIdentifier { get; init; }
}

/// <summary>
/// Source-generated serialization for both directions of the invocation.
/// </summary>
[JsonSerializable(typeof(SqsEventPayload))]
[JsonSerializable(typeof(SqsBatchResponsePayload))]
internal sealed partial class LocalSerializerContext : JsonSerializerContext;
