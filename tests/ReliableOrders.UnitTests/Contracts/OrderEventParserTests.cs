using ReliableOrders.Core.Contracts;

namespace ReliableOrders.UnitTests.Contracts;

/// <summary>
/// Covers cases 1 to 3 of the unit test list in docs/testing-strategy.md.
/// </summary>
public sealed class OrderEventParserTests
{
    private readonly OrderEventParser _parser = new();

    [Fact]
    public void Valid_sample_is_parsed()
    {
        var result = _parser.Parse(Sample.Read(Sample.Valid));

        var parsed = Assert.IsType<ParseResult.Parsed>(result);
        Assert.Equal(OrderContract.SupportedSchemaVersion, parsed.Event.SchemaVersion);
        Assert.Equal(OrderContract.ExpectedEventType, parsed.Event.EventType);
        Assert.Equal(Guid.Parse("0d76e91c-44e6-4fba-901f-bfdb76645299"), parsed.Event.EventId);
        Assert.Equal(TimeSpan.Zero, parsed.Event.OccurredAtUtc.Offset);
        Assert.Null(parsed.Event.CausationId);
        Assert.Equal("ORD-100001", parsed.Event.Data.OrderId);
        Assert.Equal("GBP", parsed.Event.Data.Currency);
        Assert.Equal(1299, parsed.Event.Data.AmountMinor);
    }

    /// <summary>
    /// Asserted here rather than assumed by Story 1.3, which classifies on these properties.
    /// </summary>
    [Fact]
    public void Republished_sample_carries_the_same_order_under_a_new_event_id()
    {
        var original = ParseEvent(Sample.Valid);
        var republished = ParseEvent(Sample.Republished);

        Assert.NotEqual(original.EventId, republished.EventId);
        Assert.NotEqual(original.OccurredAtUtc, republished.OccurredAtUtc);
        Assert.Equal(original.Data, republished.Data);
        Assert.Equal(original.EventId, republished.CausationId);
    }

    [Fact]
    public void Duplicate_sample_is_the_same_event_as_the_valid_sample()
    {
        Assert.Equal(ParseEvent(Sample.Valid), ParseEvent(Sample.Duplicate));
    }

    /// <summary>
    /// The conflicting fixture must differ from the republished one in exactly one business field,
    /// or Story 2.3's conflict tests no longer isolate what causes the conflict.
    /// </summary>
    [Fact]
    public void Conflicting_sample_differs_from_the_republish_only_in_amount()
    {
        var republished = ParseEvent(Sample.Republished).Data;
        var conflicting = ParseEvent(Sample.Conflicting).Data;

        Assert.NotEqual(republished.AmountMinor, conflicting.AmountMinor);
        Assert.Equal(republished with { AmountMinor = conflicting.AmountMinor }, conflicting);
    }

    /// <summary>
    /// The invalid fixture must reach the validator rather than stop at the parser. A body that
    /// breaks the contract rules is a permanent failure, not a malformed message.
    /// </summary>
    [Fact]
    public void Invalid_sample_parses_so_that_validation_is_what_rejects_it()
    {
        Assert.IsType<ParseResult.Parsed>(_parser.Parse(Sample.Read(Sample.Invalid)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void Blank_body_is_malformed(string body)
    {
        AssertMalformed(body, ParseFailureReason.EmptyBody);
    }

    [Fact]
    public void Null_body_is_malformed()
    {
        AssertMalformed(null, ParseFailureReason.EmptyBody);
    }

    /// <summary>
    /// Pins the decision in OrderEventParser.Parse: a BOM is stripped rather than rejected.
    /// </summary>
    [Fact]
    public void Leading_byte_order_mark_is_stripped_rather_than_rejected()
    {
        var body = '\uFEFF' + Sample.Read(Sample.Valid);

        var parsed = Assert.IsType<ParseResult.Parsed>(_parser.Parse(body));
        Assert.Equal(ParseEvent(Sample.Valid), parsed.Event);
    }

    [Fact]
    public void Body_of_nothing_but_a_byte_order_mark_is_malformed()
    {
        AssertMalformed("\uFEFF", ParseFailureReason.EmptyBody);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("not json at all")]
    [InlineData("{\"schemaVersion\": 1,}")]
    public void Malformed_json_is_rejected(string body)
    {
        AssertMalformed(body, ParseFailureReason.InvalidJson);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"a string\"")]
    [InlineData("42")]
    [InlineData("null")]
    public void Json_that_is_not_an_object_is_rejected(string body)
    {
        AssertMalformed(body, ParseFailureReason.RootNotObject);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"schemaVersion\": \"1\"}")]
    [InlineData("{\"schemaVersion\": null}")]
    [InlineData("{\"schemaVersion\": 1.5}")]
    public void Unreadable_schema_version_is_rejected(string body)
    {
        AssertMalformed(body, ParseFailureReason.SchemaVersionUnreadable);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(99)]
    [InlineData(-1)]
    public void Unsupported_schema_version_is_rejected_explicitly(int schemaVersion)
    {
        var body = SampleWith(Sample.Valid, "\"schemaVersion\": 1", $"\"schemaVersion\": {schemaVersion}");

        var result = _parser.Parse(body);

        var unsupported = Assert.IsType<ParseResult.UnsupportedSchemaVersion>(result);
        Assert.Equal(schemaVersion, unsupported.SchemaVersion);
    }

    /// <summary>
    /// A later version may change any field's shape, so the version must be classified before the
    /// envelope is bound. Otherwise this body reports as malformed.
    /// </summary>
    [Fact]
    public void Unsupported_schema_version_wins_over_a_shape_this_build_cannot_bind()
    {
        const string body = """
            {
              "schemaVersion": 2,
              "eventId": "0d76e91c-44e6-4fba-901f-bfdb76645299",
              "data": { "amountMinor": { "value": 1299, "scale": 2 } }
            }
            """;

        var unsupported = Assert.IsType<ParseResult.UnsupportedSchemaVersion>(_parser.Parse(body));
        Assert.Equal(2, unsupported.SchemaVersion);
    }

    [Fact]
    public void Field_of_the_wrong_type_is_rejected()
    {
        var body = SampleWith(Sample.Valid, "\"amountMinor\": 1299", "\"amountMinor\": \"not a number\"");

        AssertMalformed(body, ParseFailureReason.FieldTypeMismatch);
    }

    [Fact]
    public void Body_beyond_the_size_bound_is_rejected_without_being_parsed()
    {
        var body = new string('x', OrderContract.MaxMessageBodyCharacters + 1);

        AssertMalformed(body, ParseFailureReason.BodyTooLarge);
    }

    /// <summary>
    /// Unknown top-level fields are tolerated for forward compatibility. The same tolerance drops
    /// them during canonicalisation, so they cannot affect either hash. See docs/event-contract.md.
    /// </summary>
    [Fact]
    public void Unknown_top_level_fields_are_tolerated()
    {
        var body = SampleWith(Sample.Valid, "\"schemaVersion\": 1,", "\"schemaVersion\": 1, \"tenantId\": \"T-1\",");

        var parsed = Assert.IsType<ParseResult.Parsed>(_parser.Parse(body));
        Assert.Equal(ParseEvent(Sample.Valid), parsed.Event);
    }

    /// <summary>
    /// Case 25 of docs/testing-strategy.md. A reason is logged and used as a metric dimension, so
    /// it must not carry payload content.
    /// </summary>
    [Fact]
    public void Failure_reason_never_echoes_the_body()
    {
        // Well-formed on purpose. A malformed body fails before the reader looks at any value, so it
        // could not echo one. A type mismatch is the path where JsonException carries the location.
        const string secret = "4111111111111111";
        var body = SampleWith(Sample.Valid, "\"amountMinor\": 1299", $"\"amountMinor\": \"{secret}\"");

        var malformed = Assert.IsType<ParseResult.Malformed>(_parser.Parse(body));

        // Containment first, so a leak fails on the claim this test is named for. A JsonException
        // usually discloses the path rather than the value, so both are checked.
        Assert.DoesNotContain(secret, malformed.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("amountMinor", malformed.Reason, StringComparison.Ordinal);
        Assert.Equal(ParseFailureReason.FieldTypeMismatch, malformed.Reason);
    }

    private void AssertMalformed(string? body, string expectedReason)
    {
        var malformed = Assert.IsType<ParseResult.Malformed>(_parser.Parse(body));
        Assert.Equal(expectedReason, malformed.Reason);
    }

    private OrderCreatedV1 ParseEvent(string sampleFileName) =>
        Assert.IsType<ParseResult.Parsed>(_parser.Parse(Sample.Read(sampleFileName))).Event;

    /// <summary>
    /// Builds a variant of a sample by substitution, asserting the text being replaced is present.
    /// Without that assertion a reformatted sample turns the edit into a no-op, leaving any test
    /// that expects the body to parse passing while exercising nothing.
    /// </summary>
    private static string SampleWith(string sampleFileName, string find, string replaceWith)
    {
        var body = Sample.Read(sampleFileName);

        Assert.Contains(find, body, StringComparison.Ordinal);

        return body.Replace(find, replaceWith, StringComparison.Ordinal);
    }
}
