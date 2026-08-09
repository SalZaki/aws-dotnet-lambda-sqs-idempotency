using Amazon.Lambda.SQSEvents;
using ReliableOrders.Function.Sqs;

namespace ReliableOrders.UnitTests.Processing;

/// <summary>
/// The transport mapping, including every value SQS may omit.
/// </summary>
public sealed class IncomingMessageMapperTests
{
    [Fact]
    public void A_record_maps_onto_the_shape_processing_uses()
    {
        var mapped = Record(body: "{}", receiveCount: "2").ToIncomingMessage();

        Assert.Equal(MessageId, mapped.MessageId);
        Assert.Equal("{}", mapped.Body);
        Assert.Equal(2, mapped.ApproximateReceiveCount);
    }

    /// <summary>
    /// An absent body arrives as an empty one.
    /// </summary>
    /// <remarks>
    /// The parser reports <c>body.empty</c> for a null body and an empty one alike, so nothing a
    /// caller could act on is lost by coalescing here — and the processor is spared a nullable it
    /// would have to re-decide.
    /// </remarks>
    [Fact]
    public void An_absent_body_maps_to_an_empty_string()
    {
        var mapped = Record(body: null, receiveCount: "1").ToIncomingMessage();

        Assert.Equal(string.Empty, mapped.Body);
    }

    /// <summary>
    /// An unreadable receive count is treated as a first delivery.
    /// </summary>
    /// <remarks>
    /// The direction is deliberate. The first-receipt gate publishes a permanent-failure metric when
    /// the count is one, so defaulting there produces a data point rather than swallowing one: an
    /// over-count is a duplicate point on a dashboard, an under-count is a poison message no alarm
    /// ever mentions.
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a number")]
    [InlineData("0")]
    [InlineData("-3")]
    public void An_unreadable_receive_count_is_treated_as_a_first_delivery(string? raw)
    {
        var mapped = Record(body: "{}", receiveCount: raw).ToIncomingMessage();

        Assert.Equal(1, mapped.ApproximateReceiveCount);
    }

    /// <summary>
    /// String attributes are carried; anything without a string value is not.
    /// </summary>
    /// <remarks>
    /// They exist for trace context, which the publisher writes as W3C headers. A binary value has no
    /// reader here, and encoding one on every message would be paid for on the path where log and
    /// metric volume is already the dominant cost.
    /// </remarks>
    [Fact]
    public void Only_string_attributes_are_carried()
    {
        var record = Record(body: "{}", receiveCount: "1");

        record.MessageAttributes = new Dictionary<string, SQSEvent.MessageAttribute>(StringComparer.Ordinal)
        {
            ["traceparent"] = new() { StringValue = "00-trace-span-01" },
            ["blob"] = new() { StringValue = null },
        };

        var mapped = record.ToIncomingMessage();

        Assert.Equal("00-trace-span-01", Assert.Contains("traceparent", mapped.Attributes));
        Assert.DoesNotContain("blob", mapped.Attributes);
    }

    /// <summary>
    /// An attribute is found whatever casing the publisher used.
    /// </summary>
    /// <remarks>
    /// The only documented reader is trace context, W3C header names are case-insensitive, and SQS
    /// preserves the publisher's casing. Matching ordinally would leave a publisher writing
    /// <c>TraceParent</c> invisible to a consumer reading <c>traceparent</c>, and the only symptom is
    /// producer and consumer traces that quietly fail to link — so the comparer is behaviour, and is
    /// pinned here rather than left to a comment.
    /// </remarks>
    [Theory]
    [InlineData("traceparent")]
    [InlineData("TraceParent")]
    [InlineData("TRACEPARENT")]
    public void An_attribute_is_found_whatever_casing_the_publisher_used(string written)
    {
        var record = Record(body: "{}", receiveCount: "1");

        record.MessageAttributes = new Dictionary<string, SQSEvent.MessageAttribute>(StringComparer.Ordinal)
        {
            [written] = new() { StringValue = "00-trace-span-01" },
        };

        var mapped = record.ToIncomingMessage();

        Assert.Equal("00-trace-span-01", Assert.Contains("traceparent", mapped.Attributes));
    }

    [Fact]
    public void A_record_with_no_attributes_maps_to_an_empty_set()
    {
        var mapped = Record(body: "{}", receiveCount: "1").ToIncomingMessage();

        Assert.Empty(mapped.Attributes);
    }

    /// <summary>
    /// A record without a message identifier is rejected where it is mapped.
    /// </summary>
    /// <remarks>
    /// The one value with no fallback: a batch response naming an identifier SQS does not recognise
    /// makes Lambda reprocess the whole batch. Failing at the boundary beats a null surfacing from a
    /// logging call three layers down.
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void A_record_without_a_message_id_is_rejected(string? messageId)
    {
        var record = Record(body: "{}", receiveCount: "1");
        record.MessageId = messageId;

        // ThrowsAny, because ThrowIfNullOrWhiteSpace reports a null as the derived
        // ArgumentNullException and a blank as ArgumentException, and both are the same rejection.
        Assert.ThrowsAny<ArgumentException>(() => record.ToIncomingMessage());
    }

    [Fact]
    public void A_null_record_is_a_caller_defect()
    {
        SQSEvent.SQSMessage? record = null;

        Assert.Throws<ArgumentNullException>(() => record!.ToIncomingMessage());
    }

    private const string MessageId = "3a1c9a02-6f28-4a1a-9d3b-1f9f6c2b7e44";

    /// <remarks>
    /// A receive count of null means the attribute is absent entirely, which is what a locally
    /// constructed record or a changed transport produces.
    /// </remarks>
    private static SQSEvent.SQSMessage Record(string? body, string? receiveCount)
    {
        var record = new SQSEvent.SQSMessage
        {
            MessageId = MessageId,
            Body = body,
        };

        if (receiveCount is not null)
        {
            record.Attributes = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [IncomingMessageMapper.ApproximateReceiveCountAttribute] = receiveCount,
            };
        }

        return record;
    }
}
