using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using ReliableOrders.Core.Persistence;

namespace ReliableOrders.Aws.DynamoDb;

/// <summary>
/// Turns the two rows into the <c>TransactWriteItems</c> request that writes them.
/// </summary>
/// <remarks>
/// <para>
/// Separate from the store so the request can be built and inspected without a client, a container or
/// a network. The determinism rule is a property of the request body, and a test that has to reach
/// DynamoDB to check it would be asserting the wrong thing in the wrong place.
/// </para>
/// <para>
/// Index 0 is the idempotency row and index 1 the order. The order is load-bearing rather than
/// stylistic: a cancelled transaction reports its reasons positionally, and classification reads index
/// 0 as the event-level check and index 1 as the order-level one. Swapping them would invert every
/// duplicate and conflict scope without failing to compile.
/// </para>
/// </remarks>
public static class OrderTransactionFactory
{
    /// <summary>
    /// Builds the request for one event.
    /// </summary>
    /// <param name="write">The two rows, already derived from the event.</param>
    /// <param name="tables">Where to write them.</param>
    /// <returns>
    /// A request that is a pure function of its arguments. Called twice with the same arguments it
    /// produces the same body, which is what lets a retry reuse the token safely.
    /// </returns>
    public static TransactWriteItemsRequest Create(OrderWriteRequest write, DynamoDbTableNames tables)
    {
        ArgumentNullException.ThrowIfNull(write);
        ArgumentNullException.ThrowIfNull(tables);

        return new TransactWriteItemsRequest
        {
            // The event identifier verbatim. DynamoDB caps this at 36 characters, which a hyphenated
            // UUID exactly fills, so there is no room to decorate it even if that were wanted.
            ClientRequestToken = write.ClientRequestToken,
            TransactItems =
            [
                ConditionalPut(
                    tables.IdempotencyTableName,
                    IdempotencyTableSchema.PartitionKey,
                    Attributes(write.IdempotencyRecord)),
                ConditionalPut(
                    tables.OrdersTableName,
                    OrderTableSchema.PartitionKey,
                    Attributes(write.Order)),
            ],
        };
    }

    /// <remarks>
    /// <c>attribute_not_exists</c> on the partition key is what makes the write a claim rather than an
    /// overwrite. Without it a redelivery would silently replace a stored order, which is the data loss
    /// this whole design exists to prevent — and it would look like success.
    /// <para>
    /// <c>ALL_OLD</c> makes a cancelled transaction return the row that refused the write.
    /// Classification compares hashes against it and never issues a follow-up read, which would cost a
    /// round trip on the commonest retry path and open a window for the row to change in between.
    /// </para>
    /// </remarks>
    private static TransactWriteItem ConditionalPut(
        string tableName,
        string partitionKey,
        Dictionary<string, AttributeValue> item) => new()
        {
            Put = new Put
            {
                TableName = tableName,
                Item = item,
                ConditionExpression = $"attribute_not_exists({partitionKey})",
                ReturnValuesOnConditionCheckFailure = ReturnValuesOnConditionCheckFailure.ALL_OLD,
            },
        };

    private static Dictionary<string, AttributeValue> Attributes(IdempotencyRecord record) =>
        new(StringComparer.Ordinal)
        {
            [IdempotencyTableSchema.IdempotencyKey] = Text(record.IdempotencyKey),
            [IdempotencyTableSchema.OrderId] = Text(record.OrderId),
            [IdempotencyTableSchema.EnvelopeSha256] = Text(record.EnvelopeSha256),
            [IdempotencyTableSchema.OccurredAtUtc] = Timestamp(record.OccurredAtUtc),
            [IdempotencyTableSchema.CompletedAtUtc] = Timestamp(record.CompletedAtUtc),
            [IdempotencyTableSchema.ExpirationEpochSeconds] = Number(record.ExpirationEpochSeconds),
        };

    private static Dictionary<string, AttributeValue> Attributes(OrderRecord record) =>
        new(StringComparer.Ordinal)
        {
            [OrderTableSchema.OrderId] = Text(record.OrderId),
            [OrderTableSchema.CustomerId] = Text(record.CustomerId),
            [OrderTableSchema.Currency] = Text(record.Currency),
            [OrderTableSchema.AmountMinor] = Number(record.AmountMinor),
            [OrderTableSchema.ItemDescription] = Text(record.ItemDescription),
            [OrderTableSchema.BusinessSha256] = Text(record.BusinessSha256),
            [OrderTableSchema.EventId] = Text(record.EventId),
            [OrderTableSchema.CorrelationId] = Text(record.CorrelationId),
            [OrderTableSchema.SchemaVersion] = Number(record.SchemaVersion),
            [OrderTableSchema.OccurredAtUtc] = Timestamp(record.OccurredAtUtc),
            [OrderTableSchema.CreatedAtUtc] = Timestamp(record.CreatedAtUtc),
        };

    private static AttributeValue Text(string value) => new() { S = value };

    /// <remarks>
    /// Invariant culture, so the digits cannot follow the machine's locale into the table. The build
    /// sets <c>InvariantGlobalization</c> anyway; stating it here means the value does not depend on
    /// that staying true.
    /// </remarks>
    private static AttributeValue Number(long value) =>
        new() { N = value.ToString(CultureInfo.InvariantCulture) };

    /// <remarks>
    /// Round-trip form with the offset written, matching how canonicalisation renders the same instant.
    /// An operator comparing a stored row against a hashed event should not have to reconcile two
    /// spellings of one timestamp. Stored as a string rather than a number because it is read by people
    /// far more often than it is compared by code.
    /// </remarks>
    private static AttributeValue Timestamp(DateTimeOffset value) =>
        new() { S = value.ToString("O", CultureInfo.InvariantCulture) };
}
