using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using ReliableOrders.Core.Persistence;

namespace ReliableOrders.IntegrationTests;

/// <summary>
/// Creates the two tables from the schema the writer uses, so the harness cannot provision a shape
/// the production code does not write.
/// </summary>
/// <remarks>
/// <para>
/// Key names come from <see cref="OrderTableSchema"/> and <see cref="IdempotencyTableSchema"/> rather
/// than from literals repeated here. A harness that provisions its own idea of the schema tests the
/// harness, and would pass while production wrote an attribute nothing indexed.
/// </para>
/// <para>
/// Only the key attributes are declared, because DynamoDB is schemaless everywhere else — non-key
/// attributes exist because an item carries them. The CDK constructs will declare the same keys plus
/// the settings that have no meaning locally, such as encryption and point-in-time recovery.
/// </para>
/// </remarks>
internal static class DynamoDbTables
{
    /// <summary>
    /// The table names used inside the container.
    /// </summary>
    /// <remarks>
    /// Fixed here rather than read from the environment. In production these are configuration because
    /// they differ per environment; inside a disposable container there is one environment, and a test
    /// that depended on an environment variable would pass or fail according to the machine it ran on.
    /// </remarks>
    internal const string OrdersTableName = "reliable-orders-test-orders";

    /// <inheritdoc cref="OrdersTableName"/>
    internal const string IdempotencyTableName = "reliable-orders-test-idempotency";

    internal static async Task CreateAsync(IAmazonDynamoDB client, CancellationToken cancellationToken)
    {
        await client.CreateTableAsync(
            Definition(OrdersTableName, OrderTableSchema.PartitionKey),
            cancellationToken);

        await client.CreateTableAsync(
            Definition(IdempotencyTableName, IdempotencyTableSchema.PartitionKey),
            cancellationToken);

        // Time to live is enabled explicitly rather than assumed. dynamodb-local accepts the setting
        // and reports it back without ever expiring anything, which is the behaviour to rely on here:
        // the tests assert that the attribute is written and named correctly, never that a row
        // disappears. TTL is cleanup, and no correctness claim rests on its timing.
        await client.UpdateTimeToLiveAsync(
            new UpdateTimeToLiveRequest
            {
                TableName = IdempotencyTableName,
                TimeToLiveSpecification = new TimeToLiveSpecification
                {
                    AttributeName = IdempotencyTableSchema.TimeToLiveAttribute,
                    Enabled = true,
                },
            },
            cancellationToken);
    }

    /// <remarks>
    /// On-demand billing, matching production. Provisioned throughput would add a capacity dimension
    /// that dynamodb-local does not enforce, so a test could pass here and throttle in production.
    /// </remarks>
    private static CreateTableRequest Definition(string tableName, string partitionKey) => new()
    {
        TableName = tableName,
        BillingMode = BillingMode.PAY_PER_REQUEST,
        KeySchema = [new KeySchemaElement(partitionKey, KeyType.HASH)],
        AttributeDefinitions = [new AttributeDefinition(partitionKey, ScalarAttributeType.S)],
    };
}
