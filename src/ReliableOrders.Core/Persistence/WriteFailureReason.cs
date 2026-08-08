namespace ReliableOrders.Core.Persistence;

/// <summary>
/// The complete set of reasons a write can fail, as fixed strings.
/// </summary>
/// <remarks>
/// <para>
/// Not SDK exception messages. Those carry request identifiers, table names and item contents, all
/// varying per call, which would make the reason useless as a metric dimension and would put stored
/// order data into the logs. These values are low-cardinality and disclose nothing about the payload.
/// </para>
/// <para>
/// The vocabulary is defined here, in the transport-neutral project, so the DynamoDB adapter has a
/// fixed set to map onto rather than inventing strings that only its own tests ever see.
/// </para>
/// </remarks>
public static class WriteFailureReason
{
    /// <summary>
    /// The same <c>eventId</c> is already claimed by a record with a different envelope hash.
    /// </summary>
    public const string EnvelopeHashMismatch = "conflict.envelope-hash-mismatch";

    /// <summary>
    /// The order already exists with a different business hash.
    /// </summary>
    public const string BusinessHashMismatch = "conflict.business-hash-mismatch";

    /// <summary>
    /// DynamoDB rejected the <c>ClientRequestToken</c> as reused with a different request body.
    /// </summary>
    public const string TokenMismatch = "conflict.token-mismatch";

    /// <summary>
    /// A condition failed but the conflicting item was not returned.
    /// </summary>
    /// <remarks>
    /// The record was removed between the condition being evaluated and the response being built,
    /// most plausibly by TTL expiry. Retried rather than classified, because neither duplicate nor
    /// conflict can be inferred from an item that is not there.
    /// </remarks>
    public const string ConflictingItemMissing = "transient.conflicting-item-missing";

    /// <summary>
    /// Another transaction touched one of the same items.
    /// </summary>
    public const string TransactionConflict = "transient.transaction-conflict";

    /// <summary>
    /// The table rejected the request for capacity reasons.
    /// </summary>
    public const string Throttled = "transient.throttled";

    /// <summary>
    /// The service failed for a reason that is not attributable to this request.
    /// </summary>
    public const string ServiceUnavailable = "transient.service-unavailable";

    /// <summary>
    /// DynamoDB rejected the request as malformed.
    /// </summary>
    /// <remarks>
    /// A defect in how the request is built, not anything about the message. Every retry produces the
    /// identical request and fails identically, which is why it is permanent and alarms.
    /// </remarks>
    public const string MalformedRequest = "permanent.malformed-request";

    /// <summary>
    /// The write exceeded an item or collection size limit.
    /// </summary>
    /// <remarks>
    /// The contract's field limits are sized to keep the worst-case item far below the 400 KB
    /// ceiling, so reaching this means a limit was raised without recalculating.
    /// </remarks>
    public const string ItemTooLarge = "permanent.item-too-large";
}
