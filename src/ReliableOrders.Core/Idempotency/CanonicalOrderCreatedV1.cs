using System.Text.Json.Serialization;
using ReliableOrders.Core.Contracts;

namespace ReliableOrders.Core.Idempotency;

/// <summary>
/// The hash input for <c>EnvelopeSha256</c>: the whole event in the exact shape that is serialized
/// and hashed, with <see cref="CanonicalOrderData"/> nested inside it.
/// </summary>
/// <remarks>
/// <para>
/// The nesting is the point. <c>BusinessSha256</c> hashes the <see cref="Data"/> instance this type
/// already holds, so there is one canonicalisation of the business payload rather than two that could
/// drift apart. CanonicalRepresentationTests asserts the business JSON appears verbatim inside the
/// envelope JSON.
/// </para>
/// <para>
/// Identifiers and the timestamp are carried as strings, already formatted by
/// <see cref="CanonicalText"/>. Their wire form is part of the hash input and belongs in one
/// reviewable place rather than in a converter that a serializer option could swap out.
/// </para>
/// <para>
/// Nothing here derives from a clock. Every value is a function of the event, which is what lets the
/// same event be hashed identically on a first attempt and on a redelivery days later.
/// </para>
/// </remarks>
internal sealed record CanonicalOrderCreatedV1(
    [property: JsonPropertyName("schemaVersion"), JsonPropertyOrder(1)] int SchemaVersion,
    [property: JsonPropertyName("eventId"), JsonPropertyOrder(2)] string EventId,
    [property: JsonPropertyName("eventType"), JsonPropertyOrder(3)] string EventType,
    [property: JsonPropertyName("occurredAtUtc"), JsonPropertyOrder(4)] string OccurredAtUtc,
    [property: JsonPropertyName("source"), JsonPropertyOrder(5)] string Source,
    [property: JsonPropertyName("correlationId"), JsonPropertyOrder(6)] string CorrelationId,
    [property: JsonPropertyName("causationId"), JsonPropertyOrder(7)] string? CausationId,
    [property: JsonPropertyName("data"), JsonPropertyOrder(8)] CanonicalOrderData Data)
{
    /// <summary>
    /// Maps a validated event into canonical form.
    /// </summary>
    /// <remarks>
    /// Unknown top-level fields do not appear, because they never survived parsing into
    /// <see cref="OrderCreatedV1"/>. Two events differing only in fields this schema version does not
    /// know about therefore hash identically and classify as duplicates of one another. That is
    /// intended — a v1 processor has no basis for treating unrecognised data as business-significant —
    /// and it is covered by UnknownFieldHashingTests.
    /// </remarks>
    internal static CanonicalOrderCreatedV1 From(OrderCreatedV1 orderEvent) => new(
        SchemaVersion: orderEvent.SchemaVersion,
        EventId: CanonicalText.Identifier(orderEvent.EventId),
        EventType: orderEvent.EventType,
        OccurredAtUtc: CanonicalText.Instant(orderEvent.OccurredAtUtc),
        Source: orderEvent.Source,
        CorrelationId: CanonicalText.Identifier(orderEvent.CorrelationId),
        CausationId: orderEvent.CausationId is { } causationId ? CanonicalText.Identifier(causationId) : null,
        Data: CanonicalOrderData.From(orderEvent.Data));
}
