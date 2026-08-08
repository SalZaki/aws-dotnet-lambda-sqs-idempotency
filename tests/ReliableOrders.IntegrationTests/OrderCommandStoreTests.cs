using System.Globalization;
using Amazon.DynamoDBv2.Model;
using ReliableOrders.Aws.DynamoDb;
using ReliableOrders.Core.Contracts;
using ReliableOrders.Core.Idempotency;
using ReliableOrders.Core.Persistence;

namespace ReliableOrders.IntegrationTests;

/// <summary>
/// The store against a real DynamoDB, writing the rows the tables were provisioned for.
/// </summary>
/// <remarks>
/// The harness tests alongside this one assert what the emulator promises. These assert what the
/// store does with it, through the same client and tables, so a mapping that compiles but writes the
/// wrong attribute name has somewhere to fail.
/// </remarks>
[Collection(DynamoDbCollectionDefinition.Name)]
[Trait(TestCategory.Name, TestCategory.Integration)]
public sealed class OrderCommandStoreTests(DynamoDbFixture fixture)
{
    private static readonly CanonicalPayloadHasher Hasher = new();

    /// <summary>
    /// The acceptance criterion. One call writes both rows, and both are readable afterwards.
    /// </summary>
    [Fact]
    public async Task A_new_order_writes_both_rows()
    {
        var orderEvent = OrderEvents.New();

        var result = await Store().TryCreateAsync(
            orderEvent,
            Hasher.ComputeHashes(orderEvent),
            TestContext.Current.CancellationToken);

        Assert.IsType<OrderWriteResult.Created>(result);

        Assert.NotEmpty(await ReadOrder(orderEvent));
        Assert.NotEmpty(await ReadClaim(orderEvent));
    }

    /// <summary>
    /// Each row carries the hash its own conditional check is classified on, under the attribute name
    /// the schema declares. A hash written under the wrong name would leave classification comparing
    /// against nothing.
    /// </summary>
    [Fact]
    public async Task Each_row_stores_the_hash_its_scope_is_classified_on()
    {
        var orderEvent = OrderEvents.New();
        var hashes = Hasher.ComputeHashes(orderEvent);

        await Store().TryCreateAsync(orderEvent, hashes, TestContext.Current.CancellationToken);

        Assert.Equal(hashes.BusinessSha256, (await ReadOrder(orderEvent))[OrderTableSchema.BusinessSha256].S);
        Assert.Equal(
            hashes.EnvelopeSha256,
            (await ReadClaim(orderEvent))[IdempotencyTableSchema.EnvelopeSha256].S);
    }

    /// <summary>
    /// The stored expiry derives from the event, never from processing time, and is stored as a number.
    /// </summary>
    /// <remarks>
    /// Asserted against a real table because time to live only works on a numeric attribute. Writing
    /// the expiry as text disables expiry silently, and only DynamoDB can report the stored type.
    /// </remarks>
    [Fact]
    public async Task The_stored_expiry_derives_from_the_event_and_is_numeric()
    {
        var orderEvent = OrderEvents.New();

        await Store().TryCreateAsync(
            orderEvent,
            Hasher.ComputeHashes(orderEvent),
            TestContext.Current.CancellationToken);

        var claim = await ReadClaim(orderEvent);
        var expiry = claim[IdempotencyTableSchema.ExpirationEpochSeconds];

        Assert.Equal(
            orderEvent.OccurredAtUtc.Add(IdempotencyRetention.Default.Duration).ToUnixTimeSeconds(),
            long.Parse(expiry.N, CultureInfo.InvariantCulture));

        Assert.Null(expiry.S);
    }

    /// <summary>
    /// Both rows record when the event happened rather than when it was processed, so a message
    /// replayed from the dead-letter queue a week later does not claim to have been placed a week late.
    /// </summary>
    [Fact]
    public async Task The_stored_timestamps_are_the_event_time()
    {
        var orderEvent = OrderEvents.New();

        await Store().TryCreateAsync(
            orderEvent,
            Hasher.ComputeHashes(orderEvent),
            TestContext.Current.CancellationToken);

        var expected = orderEvent.OccurredAtUtc.ToString("O", CultureInfo.InvariantCulture);

        Assert.Equal(expected, (await ReadOrder(orderEvent))[OrderTableSchema.CreatedAtUtc].S);
        Assert.Equal(expected, (await ReadClaim(orderEvent))[IdempotencyTableSchema.CompletedAtUtc].S);
    }

    /// <summary>
    /// A byte-identical redelivery inside the token window is accepted rather than failing, which is
    /// what makes a retry after an indeterminate response safe. Classification of the post-window case
    /// is Story 2.3.
    /// </summary>
    /// <remarks>
    /// Both attempts report <c>Created</c>, and the second created nothing. DynamoDB answers a repeated
    /// token carrying an identical body with success, so the response cannot distinguish the two and
    /// neither can this store. A caller counting orders created on <c>Created</c> therefore counts one
    /// order twice whenever a retry follows an indeterminate response — worth knowing before the
    /// metrics story treats that count as a business figure. It is bounded by the ten-minute token
    /// window and by how often an attempt ends indeterminately, and it is the token's nature rather
    /// than something correctable here.
    /// </remarks>
    [Fact]
    public async Task A_byte_identical_redelivery_does_not_fail()
    {
        var orderEvent = OrderEvents.New();
        var hashes = Hasher.ComputeHashes(orderEvent);
        var store = Store();

        var first = await store.TryCreateAsync(orderEvent, hashes, TestContext.Current.CancellationToken);
        var second = await store.TryCreateAsync(orderEvent, hashes, TestContext.Current.CancellationToken);

        Assert.IsType<OrderWriteResult.Created>(first);
        Assert.IsType<OrderWriteResult.Created>(second);
    }

    private DynamoDbOrderCommandStore Store() => new(
        fixture.Client,
        new DynamoDbTableNames(DynamoDbTables.OrdersTableName, DynamoDbTables.IdempotencyTableName),
        IdempotencyRetention.Default);


    private Task<Dictionary<string, AttributeValue>> ReadOrder(OrderCreatedV1 orderEvent) =>
        fixture.ReadItemAsync(DynamoDbTables.OrdersTableName, OrderTableSchema.PartitionKey, orderEvent.Data.OrderId, TestContext.Current.CancellationToken);

    private Task<Dictionary<string, AttributeValue>> ReadClaim(OrderCreatedV1 orderEvent) =>
        fixture.ReadItemAsync(
            DynamoDbTables.IdempotencyTableName,
            IdempotencyTableSchema.PartitionKey,
            orderEvent.EventId.ToString(),
            TestContext.Current.CancellationToken);

}
