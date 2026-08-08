namespace ReliableOrders.Core.Persistence;

/// <summary>
/// The attribute names of the orders table. Specified in the Orders Table section of
/// docs/infrastructure.md.
/// </summary>
/// <remarks>
/// <para>
/// Names only. The table's own name is configuration, supplied at the composition root from
/// <c>ORDERS_TABLE_NAME</c>, because it differs per environment. What cannot differ per environment
/// is the shape.
/// </para>
/// <para>
/// Here rather than in the adapter so that the writer, the test harness that provisions the table and
/// the CDK construct that creates it all read one source. Three components each spelling
/// <c>BusinessSha256</c> for themselves is three chances to write an order whose hash the
/// classification path cannot find, which fails as a spurious conflict on a valid republish rather
/// than as anything obviously wrong.
/// </para>
/// <para>
/// Deliberately literal strings rather than <c>nameof</c> over <see cref="OrderRecord"/>. A stored
/// attribute name must not follow a C# property rename, because the rows already written keep the old
/// one. <c>PersistenceSchemaTests</c> holds the two in step instead, so a rename fails the build and
/// becomes a decision rather than a silent migration.
/// </para>
/// </remarks>
public static class OrderTableSchema
{
    /// <summary>
    /// Partition key. One stored order per value, which is the domain-level idempotency guarantee.
    /// </summary>
    public const string PartitionKey = OrderId;

    /// <summary>The order identifier.</summary>
    public const string OrderId = "OrderId";

    /// <summary>The owning customer.</summary>
    public const string CustomerId = "CustomerId";

    /// <summary>The three-letter currency code.</summary>
    public const string Currency = "Currency";

    /// <summary>The total in the currency's minor unit.</summary>
    public const string AmountMinor = "AmountMinor";

    /// <summary>What was ordered.</summary>
    public const string ItemDescription = "ItemDescription";

    /// <summary>
    /// The hash the domain-level conditional check is classified on.
    /// </summary>
    public const string BusinessSha256 = "BusinessSha256";

    /// <summary>The event that created this order.</summary>
    public const string EventId = "EventId";

    /// <summary>Shared by every event in one logical flow.</summary>
    public const string CorrelationId = "CorrelationId";

    /// <summary>The contract version this order was written from.</summary>
    public const string SchemaVersion = "SchemaVersion";

    /// <summary>When the event happened.</summary>
    public const string OccurredAtUtc = "OccurredAtUtc";

    /// <summary>Equals <see cref="OccurredAtUtc"/>, never a wall clock.</summary>
    public const string CreatedAtUtc = "CreatedAtUtc";
}
