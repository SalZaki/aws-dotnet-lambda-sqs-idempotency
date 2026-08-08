using ReliableOrders.Aws.DynamoDb;
using ReliableOrders.Core.Contracts;
using ReliableOrders.Core.Idempotency;
using ReliableOrders.Core.Persistence;

namespace ReliableOrders.IntegrationTests;

/// <summary>
/// Classification against a real DynamoDB, driven by the cancellation reasons it actually produces.
/// </summary>
/// <remarks>
/// The unit tests construct cancellation reasons by hand and cover every row of the table. These prove
/// the reasons DynamoDB really returns reach those rules in the shape they expect — that the codes are
/// spelled as assumed, that the conflicting item arrives, and that index 0 is the claim.
/// </remarks>
[Collection(DynamoDbCollectionDefinition.Name)]
[Trait(TestCategory.Name, TestCategory.Integration)]
public sealed class OrderClassificationTests(DynamoDbFixture fixture)
{
    private static readonly CanonicalPayloadHasher Hasher = new();

    /// <summary>
    /// The same event arriving again once the token window has passed. The envelope matches, so the
    /// first delivery already stored the order and this one is acknowledged.
    /// </summary>
    /// <remarks>
    /// Seeded through a separate token rather than by calling the store twice. Inside the ten-minute
    /// window DynamoDB answers a repeated token with success and no cancellation at all, so calling
    /// twice would exercise the token rather than the conditional check this test is about. Seeding
    /// uses the request the store itself builds, so the stored rows are the ones it would have written.
    /// </remarks>
    [Fact]
    public async Task A_repeated_event_is_an_event_duplicate()
    {
        var orderEvent = OrderEvents.New();
        await SeedAsync(orderEvent);

        var result = await Store().TryCreateAsync(
            orderEvent,
            Hasher.ComputeHashes(orderEvent),
            TestContext.Current.CancellationToken);

        Assert.Equal(DuplicateScope.Event, Assert.IsType<OrderWriteResult.Duplicate>(result).Scope);
    }

    /// <summary>
    /// One event identifier reused for different data. Permanent, and no retry settles it.
    /// </summary>
    [Fact]
    public async Task A_reused_event_id_carrying_different_data_is_an_event_conflict()
    {
        var original = OrderEvents.New();
        await SeedAsync(original);

        var diverged = original with { Data = original.Data with { AmountMinor = original.Data.AmountMinor + 1 } };

        var result = await Store().TryCreateAsync(
            diverged,
            Hasher.ComputeHashes(diverged),
            TestContext.Current.CancellationToken);

        var conflict = Assert.IsType<OrderWriteResult.Conflict>(result);

        Assert.Equal(ConflictScope.Event, conflict.Scope);
        Assert.Equal(WriteFailureReason.EnvelopeHashMismatch, conflict.Reason);
    }

    /// <summary>
    /// The reason two hashes exist. A republish carries a new event identifier and a later timestamp,
    /// and must be acknowledged rather than dead-lettered with a high-severity alarm.
    /// </summary>
    [Fact]
    public async Task A_republished_order_is_an_order_duplicate_not_a_conflict()
    {
        var original = OrderEvents.New();
        var store = Store();

        await store.TryCreateAsync(
            original,
            Hasher.ComputeHashes(original),
            TestContext.Current.CancellationToken);

        var republished = original with
        {
            EventId = Guid.NewGuid(),
            OccurredAtUtc = original.OccurredAtUtc.AddMinutes(35),
            CorrelationId = Guid.NewGuid(),
        };

        var result = await store.TryCreateAsync(
            republished,
            Hasher.ComputeHashes(republished),
            TestContext.Current.CancellationToken);

        Assert.Equal(DuplicateScope.Order, Assert.IsType<OrderWriteResult.Duplicate>(result).Scope);
    }

    [Fact]
    public async Task An_existing_order_with_different_data_is_an_order_conflict()
    {
        var original = OrderEvents.New();
        var store = Store();

        await store.TryCreateAsync(
            original,
            Hasher.ComputeHashes(original),
            TestContext.Current.CancellationToken);

        var diverged = original with
        {
            EventId = Guid.NewGuid(),
            Data = original.Data with { AmountMinor = 9999 },
        };

        var result = await store.TryCreateAsync(
            diverged,
            Hasher.ComputeHashes(diverged),
            TestContext.Current.CancellationToken);

        var conflict = Assert.IsType<OrderWriteResult.Conflict>(result);

        Assert.Equal(ConflictScope.Order, conflict.Scope);
        Assert.Equal(WriteFailureReason.BusinessHashMismatch, conflict.Reason);
    }

    /// <summary>
    /// Two publishers racing to create one order. Exactly one creation, the other a duplicate, and one
    /// order row afterwards.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Distinct event identifiers, so the transaction's token cannot be what resolves the race — the
    /// conditional put on the order key is. That is the durable safeguard, and the token is only a
    /// ten-minute convenience over it.
    /// </para>
    /// <para>
    /// Transient losers are retried before anything is asserted, because that is what the design says
    /// happens to them. Either attempt may lose to a <c>TransactionConflict</c> rather than to the
    /// condition, and both can lose it at once, so asserting an outcome on the first pass would encode
    /// a race into the expectation and fail intermittently on real DynamoDB while passing here.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Two_concurrent_publishes_of_one_order_produce_one_order()
    {
        var first = OrderEvents.New();
        var second = first with { EventId = Guid.NewGuid(), CorrelationId = Guid.NewGuid() };
        var store = Store();

        var raced = await Task.WhenAll(
            store.TryCreateAsync(first, Hasher.ComputeHashes(first), TestContext.Current.CancellationToken),
            store.TryCreateAsync(second, Hasher.ComputeHashes(second), TestContext.Current.CancellationToken));

        // Never two creations for one order, whatever the interleaving. This is the invariant the
        // conditional put exists to hold, and it is asserted before any retry can obscure it.
        Assert.True(
            raced.OfType<OrderWriteResult.Created>().Count() <= 1,
            "Both attempts reported Created for one order.");

        var settled = new[]
        {
            await SettleAsync(store, first, raced[0]),
            await SettleAsync(store, second, raced[1]),
        };

        Assert.Single(settled.OfType<OrderWriteResult.Created>());

        Assert.Equal(
            DuplicateScope.Order,
            Assert.IsType<OrderWriteResult.Duplicate>(settled.Single(r => r is OrderWriteResult.Duplicate)).Scope);

        Assert.NotEmpty(await fixture.ReadItemAsync(
            DynamoDbTables.OrdersTableName,
            OrderTableSchema.PartitionKey,
            first.Data.OrderId,
            TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Retries a transient result the way the pipeline would, until it settles.
    /// </summary>
    /// <remarks>
    /// Bounded rather than open-ended, so a genuinely stuck result fails the test instead of hanging
    /// it. A settled result is returned untouched.
    /// </remarks>
    private static async Task<OrderWriteResult> SettleAsync(
        DynamoDbOrderCommandStore store,
        OrderCreatedV1 orderEvent,
        OrderWriteResult result)
    {
        for (var attempt = 0; result is OrderWriteResult.TransientFault && attempt < 5; attempt++)
        {
            result = await store.TryCreateAsync(
                orderEvent,
                Hasher.ComputeHashes(orderEvent),
                TestContext.Current.CancellationToken);
        }

        Assert.IsNotType<OrderWriteResult.TransientFault>(result);

        return result;
    }

    /// <summary>
    /// Writes the rows the store would write, under a token it will not reuse, so the next call meets
    /// the conditional check rather than the token's ten-minute memory.
    /// </summary>
    private async Task SeedAsync(OrderCreatedV1 orderEvent)
    {
        var request = OrderTransactionFactory.Create(
            new OrderWriteRequest(orderEvent, Hasher.ComputeHashes(orderEvent), IdempotencyRetention.Default),
            Tables);

        request.ClientRequestToken = Guid.NewGuid().ToString("D");

        await fixture.Client.TransactWriteItemsAsync(request, TestContext.Current.CancellationToken);
    }

    private static DynamoDbTableNames Tables { get; } =
        new(DynamoDbTables.OrdersTableName, DynamoDbTables.IdempotencyTableName);

    private DynamoDbOrderCommandStore Store() => new(fixture.Client, Tables, IdempotencyRetention.Default);


}
