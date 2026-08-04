using ReliableOrders.Core.Contracts;

namespace ReliableOrders.UnitTests.Contracts;

public sealed class ParseResultTests
{
    private static readonly OrderCreatedV1 AnyEvent = new(
        SchemaVersion: 1,
        EventId: Guid.Parse("0d76e91c-44e6-4fba-901f-bfdb76645299"),
        EventType: OrderContract.ExpectedEventType,
        OccurredAtUtc: DateTimeOffset.UnixEpoch,
        Source: "test",
        CorrelationId: Guid.Empty,
        CausationId: null,
        Data: new OrderData("ORD-1", "CUS-1", "GBP", 1, "thing"));

    /// <summary>
    /// Each case must reach its own handler and no other.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryCase))]
    public void Match_dispatches_to_the_handler_for_the_case(ParseResult result, string expected)
    {
        var dispatched = result.Match(
            whenParsed: _ => "parsed",
            whenMalformed: _ => "malformed",
            whenUnsupportedSchemaVersion: _ => "unsupported");

        Assert.Equal(expected, dispatched);
    }

    [Fact]
    public void Match_hands_the_case_its_own_data()
    {
        ParseResult result = new ParseResult.UnsupportedSchemaVersion(7);

        var version = result.Match(
            whenParsed: _ => -1,
            whenMalformed: _ => -1,
            whenUnsupportedSchemaVersion: unsupported => unsupported.SchemaVersion);

        Assert.Equal(7, version);
    }

    public static TheoryData<ParseResult, string> EveryCase() => new()
    {
        { new ParseResult.Parsed(AnyEvent), "parsed" },
        { new ParseResult.Malformed(ParseFailureReason.InvalidJson), "malformed" },
        { new ParseResult.UnsupportedSchemaVersion(2), "unsupported" },
    };
}
