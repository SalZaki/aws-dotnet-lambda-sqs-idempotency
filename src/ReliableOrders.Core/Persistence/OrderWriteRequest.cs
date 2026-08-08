using ReliableOrders.Core.Contracts;
using ReliableOrders.Core.Idempotency;

namespace ReliableOrders.Core.Persistence;

/// <summary>
/// Everything one transaction writes for one event, derived from the event alone.
/// </summary>
/// <remarks>
/// <para>
/// The two rows are built together and handed out together, so neither can be assembled from a
/// different event than the other. That is the failure this type exists to prevent: an idempotency
/// row claiming one event while the order beside it describes another would commit atomically and be
/// wrong forever, with no check anywhere able to notice.
/// </para>
/// <para>
/// Nothing here reads a clock, and no <see cref="TimeProvider"/> can be supplied. DynamoDB raises
/// <c>IdempotentParameterMismatchException</c> when a token is reused inside its ten-minute window
/// with a different request body, so a wall-clock value anywhere in these rows would turn a routine
/// retry of a valid event into an error. <c>TimeProvider</c> stays injected elsewhere for latency
/// metrics and the invocation deadline, neither of which is persisted.
/// </para>
/// </remarks>
public sealed record OrderWriteRequest
{
    /// <summary>
    /// The length DynamoDB allows a <c>ClientRequestToken</c>.
    /// </summary>
    /// <remarks>
    /// A hyphenated UUID is exactly 36 characters, so the token fits with no headroom. That is why
    /// <see cref="ClientRequestToken"/> is the event identifier verbatim — any prefix, namespace or
    /// environment decoration would overflow the limit and fail every request.
    /// </remarks>
    public const int MaxClientRequestTokenLength = 36;

    /// <summary>
    /// Builds both rows for a validated event.
    /// </summary>
    /// <param name="orderEvent">An event that has already passed validation.</param>
    /// <param name="hashes">The hashes computed for that same event.</param>
    /// <param name="retention">How long the idempotency row is kept.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The event's <c>occurredAtUtc</c> is late enough that adding <paramref name="retention"/> leaves
    /// the representable range. The skew window rejects such an event long before this point.
    /// </exception>
    public OrderWriteRequest(OrderCreatedV1 orderEvent, PayloadHashes hashes, IdempotencyRetention retention)
    {
        ArgumentNullException.ThrowIfNull(orderEvent);
        ArgumentNullException.ThrowIfNull(hashes);
        ArgumentNullException.ThrowIfNull(retention);

        IdempotencyRecord = new IdempotencyRecord(orderEvent, hashes, retention);
        Order = new OrderRecord(orderEvent, hashes);
    }

    /// <summary>
    /// The row written at index 0 of the transaction, conditional on the event not being claimed.
    /// </summary>
    public IdempotencyRecord IdempotencyRecord { get; }

    /// <summary>
    /// The row written at index 1 of the transaction, conditional on the order not existing.
    /// </summary>
    public OrderRecord Order { get; }

    /// <summary>
    /// The transaction's <c>ClientRequestToken</c>, which is the idempotency key itself.
    /// </summary>
    /// <remarks>
    /// Named separately because the two answer different questions — one is a durable partition key,
    /// the other a ten-minute deduplication hint — and a reader who sees only the key at the call site
    /// has no way to tell that passing it as the token was deliberate rather than convenient. The
    /// value is shared because <see cref="MaxClientRequestTokenLength"/> leaves room for nothing else.
    /// </remarks>
    public string ClientRequestToken => IdempotencyRecord.IdempotencyKey;
}
