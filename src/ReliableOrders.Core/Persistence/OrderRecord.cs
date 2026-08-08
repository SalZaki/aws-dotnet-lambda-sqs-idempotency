using ReliableOrders.Core.Contracts;
using ReliableOrders.Core.Idempotency;

namespace ReliableOrders.Core.Persistence;

/// <summary>
/// The item written to the orders table, one row per order. Specified in the Orders Table section of
/// docs/infrastructure.md.
/// </summary>
/// <remarks>
/// <para>
/// The only constructor is internal and copies every value from the event. An adapter that could set
/// <see cref="CreatedAtUtc"/> from a clock would record the queue's backlog rather than the business
/// event, so an order replayed from the dead-letter queue a week later would claim to have been placed
/// a week late — and the differing request body would fail the retry outright. Adapters read this row;
/// they do not compose one.
/// </para>
/// <para>
/// The business fields are flattened rather than nested under a <c>data</c> attribute. They are what a
/// reader of this table wants, and the envelope's nesting is a transport concern that ends at parsing.
/// </para>
/// <para>
/// <see cref="BusinessSha256"/> is not diagnostic. Classification reads it back out of a failed
/// condition check to tell a benign republish from genuine divergence, so an order stored without it
/// could not be compared against a later publish at all.
/// </para>
/// </remarks>
public sealed record OrderRecord
{
    internal OrderRecord(OrderCreatedV1 orderEvent, PayloadHashes hashes)
    {
        OrderId = orderEvent.Data.OrderId;
        CustomerId = orderEvent.Data.CustomerId;
        Currency = orderEvent.Data.Currency;
        AmountMinor = orderEvent.Data.AmountMinor;
        ItemDescription = orderEvent.Data.ItemDescription;
        BusinessSha256 = hashes.BusinessSha256;
        EventId = CanonicalText.Identifier(orderEvent.EventId);
        CorrelationId = CanonicalText.Identifier(orderEvent.CorrelationId);
        SchemaVersion = orderEvent.SchemaVersion;
        OccurredAtUtc = orderEvent.OccurredAtUtc;
        CreatedAtUtc = orderEvent.OccurredAtUtc;
    }

    /// <summary>
    /// Partition key, and the domain-level idempotency key.
    /// </summary>
    public string OrderId { get; }

    /// <summary>
    /// Owning customer.
    /// </summary>
    public string CustomerId { get; }

    /// <summary>
    /// Three-letter uppercase currency code.
    /// </summary>
    public string Currency { get; }

    /// <summary>
    /// Order total in the currency's minor unit.
    /// </summary>
    public long AmountMinor { get; }

    /// <summary>
    /// Free text describing what was ordered.
    /// </summary>
    public string ItemDescription { get; }

    /// <summary>
    /// Drives domain-level classification.
    /// </summary>
    public string BusinessSha256 { get; }

    /// <summary>
    /// The event that created this order.
    /// </summary>
    /// <remarks>
    /// Rendered exactly as <see cref="IdempotencyRecord.IdempotencyKey"/> is, so the two rows can be
    /// joined by string equality without either side reformatting.
    /// </remarks>
    public string EventId { get; }

    /// <summary>
    /// Shared by every event in one logical flow.
    /// </summary>
    public string CorrelationId { get; }

    /// <summary>
    /// The contract version this order was written from.
    /// </summary>
    public int SchemaVersion { get; }

    /// <summary>
    /// When the event happened.
    /// </summary>
    public DateTimeOffset OccurredAtUtc { get; }

    /// <summary>
    /// Equals <see cref="OccurredAtUtc"/>, never a wall clock.
    /// </summary>
    public DateTimeOffset CreatedAtUtc { get; }
}
