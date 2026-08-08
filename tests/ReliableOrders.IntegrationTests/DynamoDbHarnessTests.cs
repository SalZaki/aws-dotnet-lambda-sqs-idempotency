using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using ReliableOrders.Core.Persistence;

namespace ReliableOrders.IntegrationTests;

/// <summary>
/// Proves the harness is worth building the transaction on.
/// </summary>
/// <remarks>
/// These tests assert the emulator's behaviour rather than this project's code, which is unusual and
/// deliberate. Stories 2.2 and 2.3 classify entirely from <c>CancellationReasons</c> — the reason code
/// at each index, and the conflicting item returned alongside it. If the emulator is vague about
/// either, every later test passes while proving nothing. That is exactly why the specification
/// forbids LocalStack here, so the substitute has to be held to the claim.
/// </remarks>
[Collection(DynamoDbCollectionDefinition.Name)]
[Trait(TestCategory.Name, TestCategory.Integration)]
public sealed class DynamoDbHarnessTests(DynamoDbFixture fixture)
{
    [Fact]
    public async Task Both_tables_exist_with_the_partition_key_the_writer_uses()
    {
        var orders = await fixture.Client.DescribeTableAsync(DynamoDbTables.OrdersTableName, TestContext.Current.CancellationToken);
        var idempotency = await fixture.Client.DescribeTableAsync(DynamoDbTables.IdempotencyTableName, TestContext.Current.CancellationToken);

        Assert.Equal(OrderTableSchema.PartitionKey, Assert.Single(orders.Table.KeySchema).AttributeName);
        Assert.Equal(IdempotencyTableSchema.PartitionKey, Assert.Single(idempotency.Table.KeySchema).AttributeName);
    }

    /// <summary>
    /// TTL is configured against the expiry attribute. Nothing asserts that a row disappears, because
    /// expiry is asynchronous and on DynamoDB's own schedule — TTL is cleanup, and no correctness
    /// claim rests on its timing.
    /// </summary>
    [Fact]
    public async Task Time_to_live_is_enabled_on_the_expiry_attribute()
    {
        var ttl = await fixture.Client.DescribeTimeToLiveAsync(
            DynamoDbTables.IdempotencyTableName,
            TestContext.Current.CancellationToken);

        Assert.Equal(TimeToLiveStatus.ENABLED, ttl.TimeToLiveDescription.TimeToLiveStatus);
        Assert.Equal(IdempotencyTableSchema.TimeToLiveAttribute, ttl.TimeToLiveDescription.AttributeName);
    }

    /// <summary>
    /// Two conditional puts across two tables commit together. This is the shape Story 2.2 builds, and
    /// it has to work before any classification test means anything.
    /// </summary>
    [Fact]
    public async Task A_new_order_writes_both_rows_atomically()
    {
        var key = NewKey();

        await fixture.Client.TransactWriteItemsAsync(
            Transaction(key, envelopeHash: "envelope-a", businessHash: "business-a"),
            TestContext.Current.CancellationToken);

        Assert.True(await Exists(DynamoDbTables.IdempotencyTableName, IdempotencyTableSchema.PartitionKey, key));
        Assert.True(await Exists(DynamoDbTables.OrdersTableName, OrderTableSchema.PartitionKey, key));
    }

    /// <summary>
    /// The claim this whole harness rests on. A cancelled transaction must report an accurate reason
    /// code at the failing index, and must return the conflicting item when
    /// <c>ReturnValuesOnConditionCheckFailure</c> is set.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Classification never re-reads the item afterwards. It costs a round trip on the commonest retry
    /// path and opens a window in which the row can change between the cancellation and the read, so
    /// the returned item is the only evidence available — and it has to be there.
    /// </para>
    /// <para>
    /// The second attempt carries a fresh token even though production would reuse the event
    /// identifier. That is deliberate, and it is the only place in this file where the request departs
    /// from what the writer will send. DynamoDB remembers a token for ten minutes and rejects a reused
    /// one carrying a different body outright, which is a different path covered by
    /// <see cref="Reusing_a_token_with_a_different_body_is_rejected_before_any_condition_runs"/>. What
    /// this test needs is the path taken once that window has elapsed — a redelivery of the same event
    /// hours later — where the conditional check is what refuses the write. Waiting ten minutes for it
    /// is not a test, so the token is varied instead.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_cancelled_transaction_reports_the_reason_and_returns_the_conflicting_item()
    {
        var key = NewKey();

        await fixture.Client.TransactWriteItemsAsync(
            Transaction(key, envelopeHash: "envelope-a", businessHash: "business-a"),
            TestContext.Current.CancellationToken);

        var cancelled = await Assert.ThrowsAsync<TransactionCanceledException>(
            () => fixture.Client.TransactWriteItemsAsync(
                Transaction(
                    idempotencyKey: key,
                    orderId: key,
                    envelopeHash: "envelope-b",
                    businessHash: "business-b",
                    clientRequestToken: NewKey()),
                TestContext.Current.CancellationToken));

        Assert.Equal(2, cancelled.CancellationReasons.Count);

        var idempotencyReason = cancelled.CancellationReasons[0];

        Assert.Equal("ConditionalCheckFailed", idempotencyReason.Code);

        Assert.NotNull(idempotencyReason.Item);

        Assert.Equal(
            "envelope-a",
            idempotencyReason.Item[IdempotencyTableSchema.EnvelopeSha256].S);
    }

    /// <summary>
    /// Reusing a token with a different body is refused before any condition is evaluated, as a
    /// distinct exception type rather than a cancellation.
    /// </summary>
    /// <remarks>
    /// Because the token is the event identifier and the request body is a pure function of the event,
    /// this can only mean one event identifier carried two different payloads. Story 2.3 maps it to
    /// <c>Conflict(ConflictScope.TokenMismatch)</c> rather than letting it fall into the transient
    /// bucket that catches unrecognised SDK exceptions, because retrying it can never succeed. There
    /// are no <c>CancellationReasons</c> to read here, which is exactly why it needs its own branch.
    /// </remarks>
    [Fact]
    public async Task Reusing_a_token_with_a_different_body_is_rejected_before_any_condition_runs()
    {
        var key = NewKey();

        await fixture.Client.TransactWriteItemsAsync(
            Transaction(key, envelopeHash: "envelope-a", businessHash: "business-a"),
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<IdempotentParameterMismatchException>(
            () => fixture.Client.TransactWriteItemsAsync(
                Transaction(key, envelopeHash: "envelope-b", businessHash: "business-b"),
                TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// The reasons are positionally aligned with the request's items, which is what lets index 0 mean
    /// the event-level check and index 1 the order-level one. Classification reads them by index and
    /// would invert the two scopes if that were not so.
    /// </summary>
    [Fact]
    public async Task Cancellation_reasons_align_positionally_with_the_request()
    {
        var existingOrder = NewKey();

        await fixture.Client.TransactWriteItemsAsync(
            Transaction(existingOrder, envelopeHash: "envelope-a", businessHash: "business-a"),
            TestContext.Current.CancellationToken);

        // A new event for an order that already exists: index 0 succeeds, index 1 is the failure.
        var cancelled = await Assert.ThrowsAsync<TransactionCanceledException>(
            () => fixture.Client.TransactWriteItemsAsync(
                Transaction(
                    idempotencyKey: NewKey(),
                    orderId: existingOrder,
                    envelopeHash: "envelope-c",
                    businessHash: "business-c"),
                TestContext.Current.CancellationToken));

        Assert.Equal("None", cancelled.CancellationReasons[0].Code);
        Assert.Equal("ConditionalCheckFailed", cancelled.CancellationReasons[1].Code);

        Assert.Equal(
            "business-a",
            cancelled.CancellationReasons[1].Item[OrderTableSchema.BusinessSha256].S);
    }

    /// <summary>
    /// A repeated token with an identical body is accepted rather than rejected, which is what makes a
    /// retry after an indeterminate response safe inside the ten-minute window.
    /// </summary>
    [Fact]
    public async Task An_identical_request_under_the_same_token_is_accepted()
    {
        var key = NewKey();
        var request = Transaction(key, envelopeHash: "envelope-a", businessHash: "business-a");

        await fixture.Client.TransactWriteItemsAsync(request, TestContext.Current.CancellationToken);
        await fixture.Client.TransactWriteItemsAsync(request, TestContext.Current.CancellationToken);

        Assert.True(await Exists(DynamoDbTables.OrdersTableName, OrderTableSchema.PartitionKey, key));
    }

    /// <summary>
    /// Each test uses its own identifiers, so the shared container needs no cleanup between them and
    /// one test cannot pass because of a row another wrote.
    /// </summary>
    private static string NewKey() => Guid.NewGuid().ToString("D");

    private static TransactWriteItemsRequest Transaction(
        string key,
        string envelopeHash,
        string businessHash) => Transaction(key, key, envelopeHash, businessHash);

    /// <remarks>
    /// Deliberately minimal rows — the partition keys and the two hashes. Mapping a full
    /// <see cref="OrderWriteRequest"/> onto attributes is Story 2.2's work, and writing a second
    /// version of it here would be the thing that version is later checked against.
    /// </remarks>
    private static TransactWriteItemsRequest Transaction(
        string idempotencyKey,
        string orderId,
        string envelopeHash,
        string businessHash,
        string? clientRequestToken = null) => new()
        {
            // The token is the event identifier verbatim, exactly as the transaction will carry it.
            // One test overrides it to reach the post-token-window path, and says why it has to.
            ClientRequestToken = clientRequestToken ?? idempotencyKey,
            TransactItems =
        [
            new TransactWriteItem
            {
                Put = new Put
                {
                    TableName = DynamoDbTables.IdempotencyTableName,
                    Item = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                    {
                        [IdempotencyTableSchema.IdempotencyKey] = new() { S = idempotencyKey },
                        [IdempotencyTableSchema.EnvelopeSha256] = new() { S = envelopeHash },
                    },
                    ConditionExpression = $"attribute_not_exists({IdempotencyTableSchema.PartitionKey})",
                    ReturnValuesOnConditionCheckFailure = ReturnValuesOnConditionCheckFailure.ALL_OLD,
                },
            },
            new TransactWriteItem
            {
                Put = new Put
                {
                    TableName = DynamoDbTables.OrdersTableName,
                    Item = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                    {
                        [OrderTableSchema.OrderId] = new() { S = orderId },
                        [OrderTableSchema.BusinessSha256] = new() { S = businessHash },
                    },
                    ConditionExpression = $"attribute_not_exists({OrderTableSchema.PartitionKey})",
                    ReturnValuesOnConditionCheckFailure = ReturnValuesOnConditionCheckFailure.ALL_OLD,
                },
            },
        ],
        };

    private async Task<bool> Exists(string tableName, string partitionKey, string value)
    {
        var response = await fixture.Client.GetItemAsync(
            tableName,
            new Dictionary<string, AttributeValue>(StringComparer.Ordinal) { [partitionKey] = new() { S = value } },
            TestContext.Current.CancellationToken);

        return response.IsItemSet;
    }
}
