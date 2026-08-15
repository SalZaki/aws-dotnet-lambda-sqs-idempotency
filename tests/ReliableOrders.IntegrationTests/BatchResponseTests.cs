using System.Globalization;
using Amazon.Lambda.SQSEvents;
using Amazon.SQS.Model;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ReliableOrders.Aws.DynamoDb;
using ReliableOrders.Aws.Sqs;
using ReliableOrders.Aws.Telemetry;
using ReliableOrders.Core.Contracts;
using ReliableOrders.Core.Idempotency;
using ReliableOrders.Core.Observability;
using ReliableOrders.Core.Persistence;
using ReliableOrders.Core.Processing;
using ReliableOrders.Core.Validation;

namespace ReliableOrders.IntegrationTests;

/// <summary>
/// The local end-to-end path: published to a real queue, processed by the real handler, written to a
/// real table, and the response acted on the way the event source mapping acts on it.
/// </summary>
/// <remarks>
/// <para>
/// Everything between the queue and the table is production code — the mapper, the handler, the
/// processor, and the store. What is substituted is the two things AWS runs: the event source
/// mapping, which <see cref="EventSourceMapping"/> stands in for, and the clock.
/// </para>
/// <para>
/// The two emulators are not interchangeable and are not treated as such. Transactions run on
/// <c>amazon/dynamodb-local</c> and no assertion here reads a cancellation reason; the queue is
/// LocalStack, which is the only SQS emulation available. The split is argued in the SQS Emulation
/// section of docs/testing-strategy.md.
/// </para>
/// </remarks>
[Collection(LocalPathCollectionDefinition.Name)]
[Trait(TestCategory.Name, TestCategory.Integration)]
[Trait(TestCategory.Name, TestCategory.RequiresLocalStackToken)]
public sealed class BatchResponseTests(LocalStackFixture sqs, DynamoDbFixture dynamoDb)
{
    /// <summary>
    /// What the invocation is told it has left, before the deadline margin is taken off it.
    /// </summary>
    /// <remarks>
    /// The function's configured timeout. The clock below never advances, so no record can reach the
    /// deadline and every record in these batches is attempted — which is what makes a deferral, if
    /// one ever appears here, a failure rather than a timing artefact.
    /// </remarks>
    private static readonly TimeSpan RemainingTime = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A batch of three, of which exactly one has to come back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The three outcomes that decide a response: an order created, the same event delivered twice,
    /// and a body that will never parse. The first two are successes and are acknowledged — a
    /// duplicate is the mechanism working, not a failure — and only the poison message is named.
    /// </para>
    /// <para>
    /// Asserted as a set rather than by position. SQS does not promise the order a receive returns
    /// messages in, so which of the two identical events creates the order and which is recognised as
    /// the duplicate is not fixed. Both orderings produce one creation and one duplicate, and neither
    /// puts either of them in the response.
    /// </para>
    /// </remarks>
    [RequiresLocalStack]
    public async Task A_mixed_batch_returns_only_the_record_that_must_be_redelivered()
    {
        var queues = await sqs.CreateQueuesAsync(Token);
        var orderEvent = OrderEvents.New();
        var body = OrderEvents.Serialize(orderEvent);

        await Publish(queues, body, body, OrderEvents.PoisonBody);

        var mapping = new EventSourceMapping(sqs.Client, queues);
        var batch = await mapping.ReceiveAsync(3, Token);

        Assert.Equal(3, batch.Count);

        var response = await Handle(batch);

        var returned = Assert.Single(response.BatchItemFailures);

        Assert.Equal(PoisonMessageIn(batch).MessageId, returned.ItemIdentifier);
    }

    /// <summary>
    /// A duplicate inside one batch is acknowledged, and the order it names is stored intact.
    /// </summary>
    /// <remarks>
    /// A duplicate is the mechanism working, so returning it would be the failure: the record would be
    /// redelivered, recognised as a duplicate again, and eventually dead-lettered for having been
    /// correct. The stored row is read as well as the response, because acknowledging both records is
    /// only right if one of them wrote the order — an empty failure list is also what a batch that
    /// silently dropped both would produce.
    /// </remarks>
    [RequiresLocalStack]
    public async Task A_duplicate_in_one_batch_is_acknowledged_rather_than_returned()
    {
        var queues = await sqs.CreateQueuesAsync(Token);
        var orderEvent = OrderEvents.New();
        var body = OrderEvents.Serialize(orderEvent);

        await Publish(queues, body, body);

        var batch = await new EventSourceMapping(sqs.Client, queues).ReceiveAsync(2, Token);

        Assert.Equal(2, batch.Count);

        Assert.Empty((await Handle(batch)).BatchItemFailures);

        var order = await dynamoDb.ReadItemAsync(
            DynamoDbTables.OrdersTableName,
            OrderTableSchema.PartitionKey,
            orderEvent.Data.OrderId,
            Token);

        Assert.Equal(orderEvent.EventId.ToString(), order[OrderTableSchema.EventId].S);
        Assert.Equal(
            new CanonicalPayloadHasher().ComputeHashes(orderEvent).BusinessSha256,
            order[OrderTableSchema.BusinessSha256].S);
    }

    /// <summary>
    /// Acting on the response leaves the returned record on the queue and nothing else.
    /// </summary>
    /// <remarks>
    /// The half of partial batch response that a unit test cannot reach. A response of the right shape
    /// still costs nine redeliveries if the mapping cannot match its identifiers, and the identifiers
    /// only mean something against the queue they came from — which is why the assertion is what comes
    /// back rather than what was returned.
    /// </remarks>
    [RequiresLocalStack]
    public async Task Only_the_returned_record_is_redelivered()
    {
        var queues = await sqs.CreateQueuesAsync(Token);
        var body = OrderEvents.Serialize(OrderEvents.New());

        await Publish(queues, body, OrderEvents.PoisonBody);

        var mapping = new EventSourceMapping(sqs.Client, queues);
        var batch = await mapping.ReceiveAsync(2, Token);

        Assert.Equal(2, batch.Count);

        await mapping.ApplyAsync(batch, await Handle(batch), Token);

        var redelivered = Assert.Single(await mapping.ReceiveAsync(2, Token));

        Assert.Equal(PoisonMessageIn(batch).MessageId, redelivered.MessageId);
        Assert.Equal(
            "2",
            redelivered.Attributes[IncomingMessageMapper.ApproximateReceiveCountAttribute]);
    }

    /// <summary>
    /// A message nothing can process reaches the dead-letter queue, and takes its full receive count
    /// to get there.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The whole loop, run to its end: the handler returns the record, the mapping leaves it on the
    /// queue, and SQS redrives it once the receives are spent. The deliberate part is that a permanent
    /// failure is returned at all — nothing about it will succeed on the next attempt, and it is
    /// returned so that it lands somewhere an operator can find rather than being acknowledged into
    /// silence.
    /// </para>
    /// <para>
    /// Every attempt is asserted to be returned, not just the last. A single early acknowledgement
    /// would empty the source queue and leave the dead-letter queue empty too, and asserting only the
    /// end state cannot tell that apart from the message never having been published.
    /// </para>
    /// </remarks>
    [RequiresLocalStack]
    public async Task A_poison_message_reaches_the_dead_letter_queue_after_its_receives_are_spent()
    {
        var queues = await sqs.CreateQueuesAsync(Token);

        await Publish(queues, OrderEvents.PoisonBody);

        var mapping = new EventSourceMapping(sqs.Client, queues);

        for (var attempt = 1; attempt <= SqsQueues.MaxReceiveCount; attempt++)
        {
            var batch = await mapping.ReceiveAsync(1, Token);
            var record = Assert.Single(batch);

            Assert.Equal(
                attempt.ToString(CultureInfo.InvariantCulture),
                record.Attributes[IncomingMessageMapper.ApproximateReceiveCountAttribute]);

            var response = await Handle(batch);

            Assert.Equal(record.MessageId, Assert.Single(response.BatchItemFailures).ItemIdentifier);

            await mapping.ApplyAsync(batch, response, Token);
        }

        Assert.Empty(await mapping.ReceiveAsync(1, Token));

        var deadLettered = await sqs.Client.ReceiveMessageAsync(
            new ReceiveMessageRequest
            {
                QueueUrl = queues.DeadLetterQueueUrl,
                MaxNumberOfMessages = 10,
                WaitTimeSeconds = 0,
            },
            Token);

        Assert.Equal(OrderEvents.PoisonBody, Assert.Single(deadLettered.Messages).Body);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <remarks>
    /// Identified by body rather than by position, for the reason the mixed-batch test gives: the
    /// order a receive returns messages in is not promised.
    /// </remarks>
    private static Message PoisonMessageIn(IReadOnlyList<Message> batch) =>
        batch.Single(message => string.Equals(message.Body, OrderEvents.PoisonBody, StringComparison.Ordinal));

    private async Task Publish(SqsQueues queues, params string[] bodies)
    {
        foreach (var body in bodies)
        {
            await sqs.Client.SendMessageAsync(queues.SourceQueueUrl, body, Token);
        }
    }

    /// <summary>
    /// Runs the batch through the real handler.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Assembled here rather than through <c>DependencyInjection.Build</c>, which reads the process
    /// environment and constructs a DynamoDB client against a real account. The graph is the same
    /// shape; what differs is where the client points and where the clock comes from.
    /// </para>
    /// <para>
    /// The clock is fixed to the instant the test events claim to have occurred, so the validator sees
    /// them as current. Real time would work today and start failing five days after these events were
    /// written, which is the sort of test failure that arrives long after the change that caused it —
    /// except that here there is no change, only a date.
    /// </para>
    /// <para>
    /// Logs and metrics go nowhere. What they contain is pinned by the unit tests, against a writer
    /// they can read back; here they would be output nobody asserts on, over a container's lifetime.
    /// </para>
    /// </remarks>
    private Task<SQSBatchResponse> Handle(IReadOnlyList<Message> batch)
    {
        var clock = new FakeTimeProvider(OrderEvents.OccurredAt);

        var log = new ProcessingLog(NullLogger<ProcessingLog>.Instance, "reliable-orders", "integration");

        var processor = new OrderMessageProcessor(
            new OrderEventParser(),
            new OrderEventValidator(clock, EventSkewWindow.Default),
            new CanonicalPayloadHasher(),
            new DynamoDbOrderCommandStore(
                dynamoDb.Client,
                new DynamoDbTableNames(DynamoDbTables.OrdersTableName, DynamoDbTables.IdempotencyTableName),
                IdempotencyRetention.Default),
            log,
            clock);

        var handler = new SqsBatchHandler(
            processor,
            new EmbeddedMetricsPublisher(TextWriter.Null, clock, "ReliableOrders", "reliable-orders", "integration"),
            log,
            clock);

        return handler.HandleAsync(
            EventSourceMapping.ToBatch(batch),
            new BatchInvocation(
                $"integration-{Guid.NewGuid():N}",
                ProcessingDeadline.From(clock.GetUtcNow(), RemainingTime)),
            Token);
    }
}
