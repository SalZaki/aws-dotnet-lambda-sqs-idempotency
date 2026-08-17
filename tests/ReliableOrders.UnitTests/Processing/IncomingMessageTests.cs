using ReliableOrders.Core.Processing;

namespace ReliableOrders.UnitTests.Processing;

/// <summary>
/// The invariants the message type enforces, whoever constructs one.
/// </summary>
/// <remarks>
/// Asserted against the constructor rather than through <c>IncomingMessageMapper</c>, which is what the
/// type's own reason for checking asks for: a shape enforced only where SQS records are mapped holds
/// for records that arrived from SQS and for nothing else. A quarantine replay, a fixture or a second
/// transport reaches the processor by this constructor, and the processor's promise not to throw for a
/// message it can classify rests on these three values being usable.
/// </remarks>
public sealed class IncomingMessageTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_message_without_an_identifier_is_rejected(string? messageId)
    {
        Assert.ThrowsAny<ArgumentException>(() => Message(messageId: messageId!));
    }

    /// <remarks>
    /// Zero as well as a negative, because zero is the value a receive count that was never read would
    /// have, and the first-receipt gate treats one as the first delivery.
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_delivery_count_below_the_first_delivery_is_rejected(int approximateReceiveCount)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Message(approximateReceiveCount: approximateReceiveCount));
    }

    /// <summary>
    /// Attributes are required, and an absence is expressed as an empty set.
    /// </summary>
    /// <remarks>
    /// The transport reads these before the per-record try opens, to start the record's span, so a null
    /// reaching that point leaves the batch handler as an exception rather than as one record's failure
    /// — failing the invocation and redelivering records that had already been committed. Rejecting it
    /// at construction is what keeps that from being reachable at all.
    /// </remarks>
    [Fact]
    public void A_message_without_an_attribute_set_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => new IncomingMessage(MessageId, Body, 1, Attributes: null!));
    }

    [Fact]
    public void A_message_carrying_no_attributes_is_ordinary()
    {
        Assert.Empty(Message().Attributes);
    }

    private const string MessageId = "b3f4c8d0-1e5a-4c2b-9f7d-6a8e0b1c2d3e";

    /// <remarks>
    /// Not a valid event, and it does not matter: nothing here reaches the parser. The body is the one
    /// value of the four this type states a fallback for rather than checking.
    /// </remarks>
    private const string Body = "{}";

    private static IncomingMessage Message(
        string messageId = MessageId,
        int approximateReceiveCount = 1) =>
        new(messageId, Body, approximateReceiveCount, new Dictionary<string, string>(StringComparer.Ordinal));
}
