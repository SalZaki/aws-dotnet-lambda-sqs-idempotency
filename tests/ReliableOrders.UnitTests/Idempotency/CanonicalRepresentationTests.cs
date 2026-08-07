using System.Text.Json;
using ReliableOrders.Core.Contracts;
using ReliableOrders.Core.Idempotency;
using ReliableOrders.UnitTests.Validation;

namespace ReliableOrders.UnitTests.Idempotency;

/// <summary>
/// The bytes that get hashed, asserted directly rather than through a hexadecimal digest.
/// </summary>
/// <remarks>
/// A hash test can only report that something changed. These tests say what the canonical form is, so
/// a change arrives in review as a readable diff of JSON rather than as a different digest.
/// </remarks>
public sealed class CanonicalRepresentationTests
{
    /// <summary>
    /// The whole canonical envelope, spelled out. Property order, property names, identifier casing,
    /// timestamp precision, the explicit null and the escaping are all decisions this string pins.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Changing this string changes every hash the build produces, which reclassifies every
    /// idempotency record already stored: replays that used to match would begin to differ, and benign
    /// redeliveries would be reported as conflicts. Treat an edit here as a schema migration.
    /// </para>
    /// <para>
    /// The offset reads <c>\u002B00:00</c> rather than <c>+00:00</c> because the serializer's default
    /// encoder escapes <c>+</c>. It is stable and it is ASCII, so it is left alone; relaxing the
    /// encoder to make the output prettier would rewrite every hash for no operational gain.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_canonical_envelope_is_the_expected_json()
    {
        Assert.Equal(
            """
            {"schemaVersion":1,"eventId":"0d76e91c-44e6-4fba-901f-bfdb76645299","eventType":"order.created","occurredAtUtc":"2026-08-01T11:55:00.0000000\u002B00:00","source":"sample.order-publisher","correlationId":"f1e02471-f9da-437f-bc32-e4e65394658a","causationId":null,"data":{"orderId":"ORD-100001","customerId":"CUS-90001","currency":"GBP","amountMinor":1299,"itemDescription":"Mechanical keyboard"}}
            """,
            CanonicalEnvelopeJson(ValidEvent.Create()));
    }

    /// <summary>
    /// The two scopes share one canonicalisation of the business payload. The business JSON is a
    /// substring of the envelope JSON because it is the same object, serialized by the same context,
    /// and that is what makes drift between the two impossible rather than merely unlikely.
    /// </summary>
    [Fact]
    public void The_business_json_appears_verbatim_inside_the_envelope_json()
    {
        var orderEvent = ValidEvent.Create();

        Assert.Contains(CanonicalBusinessJson(orderEvent), CanonicalEnvelopeJson(orderEvent), StringComparison.Ordinal);
    }

    /// <summary>
    /// An absent <c>causationId</c> is written as an explicit null rather than omitted. Omitting it
    /// would make the property's presence carry meaning, so a later contract change that started
    /// sending the field would have to decide whether an event without it hashes as it used to.
    /// </summary>
    [Fact]
    public void An_absent_causation_id_is_written_as_null()
    {
        var json = CanonicalEnvelopeJson(ValidEvent.Create() with { CausationId = null });

        Assert.Contains("\"causationId\":null", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// The offset is rendered, not folded into the instant.
    /// </summary>
    /// <remarks>
    /// Validation rejects a non-zero offset before hashing, so this event cannot occur in production.
    /// It is constructed here to prove the mechanism: converting to UTC first would make the rejected
    /// spelling hash the same as its UTC equivalent, quietly performing the normalisation the contract
    /// forbids for the reason that normalising changes the hash input.
    /// </remarks>
    [Fact]
    public void A_non_utc_offset_is_rendered_rather_than_normalised()
    {
        var utcEvent = ValidEvent.Create();
        var offsetEvent = utcEvent with { OccurredAtUtc = utcEvent.OccurredAtUtc.ToOffset(TimeSpan.FromHours(1)) };

        Assert.Contains(
            """
            "occurredAtUtc":"2026-08-01T12:55:00.0000000\u002B01:00"
            """,
            CanonicalEnvelopeJson(offsetEvent),
            StringComparison.Ordinal);

        Assert.NotEqual(CanonicalEnvelopeJson(utcEvent), CanonicalEnvelopeJson(offsetEvent));
    }

    private static string CanonicalEnvelopeJson(OrderCreatedV1 orderEvent) =>
        JsonSerializer.Serialize(
            CanonicalOrderCreatedV1.From(orderEvent),
            CanonicalSerializerContext.Default.CanonicalOrderCreatedV1);

    private static string CanonicalBusinessJson(OrderCreatedV1 orderEvent) =>
        JsonSerializer.Serialize(
            CanonicalOrderCreatedV1.From(orderEvent).Data,
            CanonicalSerializerContext.Default.CanonicalOrderData);
}
