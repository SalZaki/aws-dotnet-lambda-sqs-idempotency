using ReliableOrders.Core.Contracts;
using ReliableOrders.Core.Idempotency;

namespace ReliableOrders.UnitTests.Idempotency;

/// <summary>
/// Unknown top-level fields are tolerated by the parser, dropped by canonicalisation, and therefore
/// invisible to both hashes.
/// </summary>
/// <remarks>
/// This is a decision, not an accident. Two events differing only in fields this schema version does
/// not know about are classified as duplicates of one another, so the second is acknowledged and the
/// first one's data is what gets stored. A v1 processor has no basis on which to treat unrecognised
/// data as business-significant, and the alternative — hashing what arrived — would make every future
/// additive field a source of spurious conflicts against records written before it existed.
/// </remarks>
public sealed class UnknownFieldHashingTests
{
    private static readonly CanonicalPayloadHasher Hasher = new();

    [Fact]
    public void An_unknown_top_level_field_does_not_change_either_hash()
    {
        var known = Sample.ParseEvent(Sample.Valid);
        var extended = ParseWithAddedTopLevelFields("\"tenantId\": \"acme\",");

        Assert.Equal(Hasher.ComputeHashes(known), Hasher.ComputeHashes(extended));
    }

    /// <summary>
    /// Structured and repeated unknown fields are covered as well as a scalar one. A future contract
    /// version is as likely to add an object or an array as a string.
    /// </summary>
    [Fact]
    public void Several_unknown_top_level_fields_of_any_shape_do_not_change_either_hash()
    {
        var known = Sample.ParseEvent(Sample.Valid);
        var extended = ParseWithAddedTopLevelFields(
            """
            "tenantId": "acme",
              "traceState": { "vendor": "example", "sampled": true },
              "tags": ["priority", "gift"],
              "retryCount": 3,
              "supersededBy": null,
            """);

        Assert.Equal(Hasher.ComputeHashes(known), Hasher.ComputeHashes(extended));
    }

    /// <summary>
    /// The body's own property order and whitespace do not reach the hash either. Canonicalisation
    /// re-renders the parsed event, which is why the raw body must never be hashed: two publishers
    /// sending the same event through different serializers would otherwise disagree.
    /// </summary>
    [Fact]
    public void Reordered_and_reformatted_known_fields_do_not_change_either_hash()
    {
        var known = Sample.ParseEvent(Sample.Valid);

        var reordered = Parse(
            """
            {"data":{"itemDescription":"Mechanical keyboard","amountMinor":1299,"currency":"GBP",
            "customerId":"CUS-90001","orderId":"ORD-100001"},"causationId":null,
            "correlationId":"f1e02471-f9da-437f-bc32-e4e65394658a","source":"sample.order-publisher",
            "occurredAtUtc":"2026-08-01T10:30:00Z","eventType":"order.created",
            "eventId":"0d76e91c-44e6-4fba-901f-bfdb76645299","schemaVersion":1}
            """);

        Assert.Equal(Hasher.ComputeHashes(known), Hasher.ComputeHashes(reordered));
    }

    /// <summary>
    /// Inserts fields immediately after the opening brace of the valid fixture, so they sit before
    /// every field the contract knows about.
    /// </summary>
    private static OrderCreatedV1 ParseWithAddedTopLevelFields(string fields)
    {
        var body = Sample.Read(Sample.Valid);

        return Parse(string.Concat("{\n  ", fields, body.AsSpan(body.IndexOf('{') + 1)));
    }

    private static OrderCreatedV1 Parse(string body) =>
        Assert.IsType<ParseResult.Parsed>(new OrderEventParser().Parse(body)).Event;
}
