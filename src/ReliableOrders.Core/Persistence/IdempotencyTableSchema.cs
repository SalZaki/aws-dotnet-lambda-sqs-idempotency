namespace ReliableOrders.Core.Persistence;

/// <summary>
/// The attribute names of the idempotency table. Specified in the Idempotency Table section of
/// docs/infrastructure.md.
/// </summary>
/// <remarks>
/// Names only. The table's own name is configuration, supplied at the composition root from
/// <c>IDEMPOTENCY_TABLE_NAME</c>. See <see cref="OrderTableSchema"/> for why the shape lives here
/// rather than in the adapter, and why the values are literals rather than <c>nameof</c>.
/// </remarks>
public static class IdempotencyTableSchema
{
    /// <summary>
    /// Partition key. The event identifier verbatim, which is also the transaction's
    /// <c>ClientRequestToken</c>.
    /// </summary>
    public const string PartitionKey = IdempotencyKey;

    /// <summary>The event-level idempotency key.</summary>
    public const string IdempotencyKey = "IdempotencyKey";

    /// <summary>The order this event created, carried for triage rather than for classification.</summary>
    public const string OrderId = "OrderId";

    /// <summary>
    /// The hash the event-level conditional check is classified on.
    /// </summary>
    public const string EnvelopeSha256 = "EnvelopeSha256";

    /// <summary>When the event happened.</summary>
    public const string OccurredAtUtc = "OccurredAtUtc";

    /// <summary>Equals <see cref="OccurredAtUtc"/>, never a wall clock.</summary>
    public const string CompletedAtUtc = "CompletedAtUtc";

    /// <summary>
    /// The attribute DynamoDB's time-to-live is configured against.
    /// </summary>
    /// <remarks>
    /// Cleanup, not a correctness boundary. Expiry is asynchronous and on DynamoDB's own schedule, so
    /// nothing may assume a row disappears at the instant it expires. After it does, a replayed event
    /// falls through to the order-level check and is still classified correctly, which is only true
    /// because the order carries a business hash of its own.
    /// </remarks>
    public const string TimeToLiveAttribute = ExpirationEpochSeconds;

    /// <summary>Epoch seconds, derived from <see cref="OccurredAtUtc"/> plus the configured retention.</summary>
    public const string ExpirationEpochSeconds = "ExpirationEpochSeconds";
}
