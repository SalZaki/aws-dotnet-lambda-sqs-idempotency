using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using ReliableOrders.Core.Persistence;

namespace ReliableOrders.Local;

/// <summary>
/// Creates the two tables from the schema the writer uses, so the local stack cannot provision a
/// shape the production code does not write.
/// </summary>
/// <remarks>
/// <para>
/// Key names come from <see cref="OrderTableSchema"/> and <see cref="IdempotencyTableSchema"/> rather
/// than from literals repeated here, for the reason those types give: three components each spelling
/// an attribute name for themselves is three chances to write an order whose hash the classification
/// path cannot find.
/// </para>
/// <para>
/// Only the key attributes are declared, because DynamoDB is schemaless everywhere else. The settings
/// with no local meaning — encryption, point-in-time recovery, deletion protection — are the CDK's,
/// and restating them here would put a second, weaker copy beside the authority.
/// </para>
/// </remarks>
internal static class LocalTables
{
    /// <summary>
    /// Creates both tables, and leaves the ones a previous run created alone.
    /// </summary>
    /// <param name="client">A client pointed at the emulator.</param>
    /// <param name="ordersTableName">The table holding one row per order.</param>
    /// <param name="idempotencyTableName">The table holding one row per event.</param>
    /// <param name="cancellationToken">Forwarded to each call.</param>
    internal static async Task CreateAsync(
        IAmazonDynamoDB client,
        string ordersTableName,
        string idempotencyTableName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);

        await CreateAsync(client, Definition(ordersTableName, OrderTableSchema.PartitionKey), cancellationToken);

        await CreateAsync(
            client,
            Definition(idempotencyTableName, IdempotencyTableSchema.PartitionKey),
            cancellationToken);

        await EnableTimeToLiveAsync(client, idempotencyTableName, cancellationToken);
    }

    /// <summary>
    /// Enables time to live on the idempotency table, unless it is already on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Enabled explicitly rather than assumed. dynamodb-local accepts the setting and reports it back
    /// without ever expiring anything, which is the behaviour to rely on here: the stack shows that
    /// the attribute is written and named correctly, never that a row disappears. TTL is cleanup, and
    /// no correctness claim rests on its timing.
    /// </para>
    /// <para>
    /// Checked first because <c>UpdateTimeToLive</c> is not idempotent — it fails with "TimeToLive is
    /// already enabled" rather than returning the state it was asked for. Provisioning runs again on
    /// every <c>docker compose up</c>, so without this the second start of a stack fails on a table
    /// that is already exactly right.
    /// </para>
    /// </remarks>
    private static async Task EnableTimeToLiveAsync(
        IAmazonDynamoDB client,
        string tableName,
        CancellationToken cancellationToken)
    {
        var described = await client.DescribeTimeToLiveAsync(tableName, cancellationToken);

        // ENABLING as well as ENABLED. Real DynamoDB takes up to an hour to apply the change and
        // rejects a second request throughout, so treating the intermediate state as "not yet on"
        // would fail on the one service this stack is a rehearsal for.
        var status = described.TimeToLiveDescription?.TimeToLiveStatus;

        if (status == TimeToLiveStatus.ENABLED || status == TimeToLiveStatus.ENABLING)
        {
            Log.Line($"Time to live is already enabled on {tableName}.");

            return;
        }

        await client.UpdateTimeToLiveAsync(
            new UpdateTimeToLiveRequest
            {
                TableName = tableName,
                TimeToLiveSpecification = new TimeToLiveSpecification
                {
                    AttributeName = IdempotencyTableSchema.TimeToLiveAttribute,
                    Enabled = true,
                },
            },
            cancellationToken);
    }

    /// <remarks>
    /// An existing table is left as it is rather than deleted and recreated. Provisioning runs on
    /// every <c>docker compose up</c>, and a stack that emptied its own tables on restart would make
    /// the duplicate and conflict flows impossible to demonstrate across one — which is exactly what
    /// they are for.
    /// </remarks>
    private static async Task CreateAsync(
        IAmazonDynamoDB client,
        CreateTableRequest definition,
        CancellationToken cancellationToken)
    {
        try
        {
            await client.CreateTableAsync(definition, cancellationToken);
        }
        catch (ResourceInUseException)
        {
            Log.Line($"Table {definition.TableName} already exists.");
        }
    }

    /// <remarks>
    /// On-demand billing, matching production. Provisioned throughput would add a capacity dimension
    /// that dynamodb-local does not enforce, so the stack could run happily here and throttle in
    /// production.
    /// </remarks>
    private static CreateTableRequest Definition(string tableName, string partitionKey) => new()
    {
        TableName = tableName,
        BillingMode = BillingMode.PAY_PER_REQUEST,
        KeySchema = [new KeySchemaElement(partitionKey, KeyType.HASH)],
        AttributeDefinitions = [new AttributeDefinition(partitionKey, ScalarAttributeType.S)],
    };
}
