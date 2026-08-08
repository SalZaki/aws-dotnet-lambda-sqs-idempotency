namespace ReliableOrders.Core.Persistence;

/// <summary>
/// Which safeguard refused a write because the data disagreed with what is already stored.
/// </summary>
/// <remarks>
/// Every value is a permanent failure that raises a high-severity alarm. A conflict means two
/// different payloads claim the same identity, and no retry can resolve that — one of the two
/// publishers is wrong.
/// </remarks>
public enum ConflictScope
{
    /// <summary>
    /// The same <c>eventId</c> was already claimed, carrying a different envelope hash. One event
    /// identifier has been used for two different events.
    /// </summary>
    Event,

    /// <summary>
    /// The order already exists with a different business hash. Two publishes disagree about the
    /// contents of one order.
    /// </summary>
    Order,

    /// <summary>
    /// DynamoDB rejected the transaction's <c>ClientRequestToken</c> as reused with a different
    /// request body.
    /// </summary>
    /// <remarks>
    /// Because the request body is a pure function of the event, this can only mean the same
    /// <c>eventId</c> carried different data inside the ten-minute token window. It is mapped here
    /// rather than to the transient bucket, where an unclassified SDK exception would otherwise land,
    /// because retrying it cannot succeed.
    /// </remarks>
    TokenMismatch,
}
