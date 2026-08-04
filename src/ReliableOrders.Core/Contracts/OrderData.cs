namespace ReliableOrders.Core.Contracts;

/// <summary>
/// Business payload of an <see cref="OrderCreatedV1"/> event, and the complete input to
/// <c>BusinessSha256</c>. Identical data means the same logical order, so a republish under a new
/// event ID is a duplicate rather than a conflict.
/// </summary>
/// <param name="OrderId">Domain-level idempotency key. One stored order per value.</param>
/// <param name="CustomerId">Owning customer.</param>
/// <param name="Currency">Three-letter uppercase currency code.</param>
/// <param name="AmountMinor">
/// Order total in the currency's minor unit. Integer rather than decimal so no floating-point
/// ambiguity reaches the hash, and <see cref="long"/> so a high-precision minor unit on a large order
/// cannot overflow.
/// </param>
/// <param name="ItemDescription">Free text describing what was ordered.</param>
public sealed record OrderData(
    string OrderId,
    string CustomerId,
    string Currency,
    long AmountMinor,
    string ItemDescription);
