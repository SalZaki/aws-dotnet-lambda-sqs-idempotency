using ReliableOrders.Core.Contracts;
using ReliableOrders.Core.Idempotency;

namespace ReliableOrders.Core.Persistence;

/// <summary>
/// The item written to the idempotency table, one row per event. Specified in the Idempotency Table
/// section of docs/infrastructure.md.
/// </summary>
/// <remarks>
/// <para>
/// The only constructor is internal and derives every value from the event. That is the point of the
/// type rather than an implementation detail — an adapter that could set <see cref="CompletedAtUtc"/>
/// or <see cref="ExpirationEpochSeconds"/> from a clock would build a different request body on every
/// attempt, and DynamoDB answers a reused <c>ClientRequestToken</c> carrying a different body with
/// <c>IdempotentParameterMismatchException</c>. A routine retry of a valid event would become an
/// error. Adapters read this row; they do not compose one.
/// </para>
/// <para>
/// There is no status attribute, and that is deliberate. A status exists to tell an in-flight claim
/// from a completed one, which is the mark-then-write design this service rejects — it loses orders
/// when an invocation stops between the mark and the write. Because this row and the order commit in
/// one transaction, the only state that can ever be observed is complete, and an attribute with one
/// possible value invites a reader to assume a second one exists.
/// </para>
/// </remarks>
public sealed record IdempotencyRecord
{
    /// <exception cref="ArgumentOutOfRangeException">
    /// The event's <c>occurredAtUtc</c> is late enough that adding <paramref name="retention"/> leaves
    /// the representable range. The skew window rejects such an event long before this point.
    /// </exception>
    internal IdempotencyRecord(OrderCreatedV1 orderEvent, PayloadHashes hashes, IdempotencyRetention retention)
    {
        IdempotencyKey = CanonicalText.Identifier(orderEvent.EventId);
        OrderId = orderEvent.Data.OrderId;
        EnvelopeSha256 = hashes.EnvelopeSha256;
        OccurredAtUtc = orderEvent.OccurredAtUtc;
        CompletedAtUtc = orderEvent.OccurredAtUtc;
        ExpirationEpochSeconds = orderEvent.OccurredAtUtc.Add(retention.Duration).ToUnixTimeSeconds();
    }

    /// <summary>
    /// Partition key, and the event identifier verbatim.
    /// </summary>
    /// <remarks>
    /// Also the transaction's <c>ClientRequestToken</c>, which is why it carries no prefix or
    /// namespace. See <see cref="OrderWriteRequest.MaxClientRequestTokenLength"/>.
    /// </remarks>
    public string IdempotencyKey { get; }

    /// <summary>
    /// The order this event created.
    /// </summary>
    /// <remarks>
    /// Never read by classification. It is here because it is the first thing an operator needs when
    /// triaging a dead-lettered message or a conflict alarm, and it costs one attribute.
    /// </remarks>
    public string OrderId { get; }

    /// <summary>
    /// Drives event-level classification.
    /// </summary>
    public string EnvelopeSha256 { get; }

    /// <summary>
    /// When the event happened.
    /// </summary>
    public DateTimeOffset OccurredAtUtc { get; }

    /// <summary>
    /// Equals <see cref="OccurredAtUtc"/>.
    /// </summary>
    /// <remarks>
    /// Processing time would differ between an attempt and its retry, which the deterministic request
    /// body forbids.
    /// </remarks>
    public DateTimeOffset CompletedAtUtc { get; }

    /// <summary>
    /// TTL attribute, measured from <see cref="OccurredAtUtc"/> plus the configured retention.
    /// </summary>
    /// <remarks>
    /// Anchoring it to processing time would extend the row's life on every retry and change the
    /// request body each time.
    /// </remarks>
    public long ExpirationEpochSeconds { get; }
}
