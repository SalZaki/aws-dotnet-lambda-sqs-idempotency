using ReliableOrders.Core.Contracts;

namespace ReliableOrders.Core.Idempotency;

/// <summary>
/// Everything the transaction writes for one event, derived from the event alone.
/// </summary>
/// <remarks>
/// <para>
/// The transaction carries a deterministic <c>ClientRequestToken</c>, and DynamoDB raises
/// <c>IdempotentParameterMismatchException</c> when the same token is reused inside its ten-minute
/// window with a different request body. Every attribute the request carries must therefore be a pure
/// function of the validated event and static configuration. If a wall-clock value leaked in, two
/// attempts at the same event milliseconds apart would build different bodies and the second would
/// fail — turning a routine retry of a valid event into an error.
/// </para>
/// <para>
/// This type is where that rule is kept. It takes no clock and no <see cref="TimeProvider"/>, so a
/// store built on it cannot reintroduce the bug by reaching for one. <c>TimeProvider</c> remains
/// injected elsewhere for latency metrics and the invocation deadline, neither of which is persisted.
/// </para>
/// <para>
/// The store's own signature takes the event and its hashes; it builds the claim from those and the
/// retention it was configured with, rather than accepting a claim a caller assembled.
/// </para>
/// </remarks>
public sealed record IdempotencyClaim
{
    /// <summary>
    /// The length DynamoDB allows a <c>ClientRequestToken</c>.
    /// </summary>
    /// <remarks>
    /// A hyphenated UUID is exactly 36 characters, so the token fits with no headroom. That is why
    /// <see cref="Key"/> is the event identifier verbatim: any prefix, namespace or environment
    /// decoration would overflow the limit and fail the request.
    /// </remarks>
    public const int MaxClientRequestTokenLength = 36;

    /// <summary>
    /// Builds the claim for a validated event.
    /// </summary>
    /// <param name="orderEvent">An event that has already passed validation.</param>
    /// <param name="hashes">The hashes computed for that same event.</param>
    /// <param name="retention">How long the idempotency record is kept.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The event's <c>occurredAtUtc</c> is late enough that adding <paramref name="retention"/> leaves
    /// the representable range. The skew window rejects such an event long before this point.
    /// </exception>
    public IdempotencyClaim(OrderCreatedV1 orderEvent, PayloadHashes hashes, IdempotencyRetention retention)
    {
        ArgumentNullException.ThrowIfNull(orderEvent);
        ArgumentNullException.ThrowIfNull(hashes);
        ArgumentNullException.ThrowIfNull(retention);

        Key = CanonicalText.Identifier(orderEvent.EventId);
        Hashes = hashes;
        CreatedAtUtc = orderEvent.OccurredAtUtc;
        ExpirationEpochSeconds = orderEvent.OccurredAtUtc.Add(retention.Duration).ToUnixTimeSeconds();
    }

    /// <summary>
    /// The event-level idempotency key, written as the idempotency record's partition key.
    /// </summary>
    /// <remarks>
    /// The event identifier in hyphenated lowercase form, unaltered. Rendered by
    /// <see cref="CanonicalText.Identifier"/>, the same routine the canonical envelope uses, so the key
    /// and the identifier inside <c>EnvelopeSha256</c> are one string rather than two spellings of one
    /// value that could drift apart.
    /// </remarks>
    public string Key { get; }

    /// <summary>
    /// The transaction's <c>ClientRequestToken</c>, which is <see cref="Key"/> itself.
    /// </summary>
    /// <remarks>
    /// Named separately because the two answer different questions — one is a durable partition key,
    /// the other a ten-minute deduplication hint — and a reader who sees only <c>Key</c> at the call
    /// site has no way to tell that passing it as the token was deliberate. The value is shared
    /// because <see cref="MaxClientRequestTokenLength"/> leaves room for nothing else.
    /// </remarks>
    public string ClientRequestToken => Key;

    /// <summary>
    /// The hashes the two conditional writes are classified on.
    /// </summary>
    public PayloadHashes Hashes { get; }

    /// <summary>
    /// The creation stamp written on both items, which is the event's <c>occurredAtUtc</c>.
    /// </summary>
    /// <remarks>
    /// When the event happened, not when it was processed. Processing time would differ between an
    /// attempt and its retry, which the deterministic request body forbids; it would also record the
    /// queue's backlog rather than the business event, so an order replayed from the dead-letter queue
    /// a week later would claim to have been placed a week late.
    /// </remarks>
    public DateTimeOffset CreatedAtUtc { get; }

    /// <summary>
    /// The DynamoDB TTL attribute on the idempotency record, in epoch seconds.
    /// </summary>
    /// <remarks>
    /// Measured from <c>occurredAtUtc</c>, not from processing time. Anchoring it to the event means a
    /// redelivery computes the same expiry as the first attempt, whereas anchoring it to <c>now</c>
    /// would extend the record's life on every retry and produce a different request body each time.
    /// </remarks>
    public long ExpirationEpochSeconds { get; }
}
