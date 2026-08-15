using Amazon.SQS.Model;
using ReliableOrders.Aws.Sqs;

namespace ReliableOrders.IntegrationTests;

/// <summary>
/// What the SQS emulator promises, asserted before anything is built on it.
/// </summary>
/// <remarks>
/// The counterpart to <see cref="DynamoDbHarnessTests"/>, and there for the same reason. Three
/// behaviours of the queue carry decisions made elsewhere in this service — the receive count the
/// permanent-failure metric is gated on, the redrive that turns an unprocessable message into a
/// dead-letter entry, and the message attributes trace context travels in. If the emulator is wrong
/// about any of them, every test above it is asserting against a queue that does not behave like SQS,
/// and these are where that is found out.
/// </remarks>
[Collection(LocalPathCollectionDefinition.Name)]
[Trait(TestCategory.Name, TestCategory.Integration)]
[Trait(TestCategory.Name, TestCategory.RequiresLocalStackToken)]
public sealed class SqsHarnessTests(LocalStackFixture fixture)
{
    /// <summary>
    /// A body survives the round trip unchanged.
    /// </summary>
    /// <remarks>
    /// The least this suite can be built on. Every hash in this service is computed from the bytes of
    /// a body, so a transport that re-encoded one would produce a conflict out of a redelivery.
    /// </remarks>
    [RequiresLocalStack]
    public async Task A_published_body_is_received_unchanged()
    {
        var queues = await Queues();
        var body = OrderEvents.Serialize(OrderEvents.New());

        await Publish(queues, body);

        var received = Assert.Single(await Receive(queues, 1));

        Assert.Equal(body, received.Body);
    }

    /// <summary>
    /// A received message carries everything the mapper reads off a record.
    /// </summary>
    /// <remarks>
    /// Asserted through <see cref="IncomingMessageMapper"/> rather than against the raw message,
    /// because the mapper is what production runs and each of its fields has a fallback. A fallback
    /// taken silently is the failure to look for: the receive count defaults to a first delivery, so
    /// an emulator that omitted the attribute would leave every test above passing while the value
    /// under test came from the default.
    /// </remarks>
    [RequiresLocalStack]
    public async Task A_received_message_carries_what_the_mapper_reads()
    {
        var queues = await Queues();
        var body = OrderEvents.Serialize(OrderEvents.New());
        const string traceParent = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";

        await Publish(queues, body, traceParent);

        var received = Assert.Single(await Receive(queues, 1));
        var message = EventSourceMapping.ToBatch([received]).Records[0].ToIncomingMessage();

        Assert.Equal(received.MessageId, message.MessageId);
        Assert.Equal(body, message.Body);
        Assert.Equal(1, message.ApproximateReceiveCount);
        Assert.Equal(traceParent, message.Attributes["traceparent"]);
    }

    /// <summary>
    /// A message put back on the queue comes back with its receive count incremented.
    /// </summary>
    /// <remarks>
    /// This is the value the permanent-failure metric is gated on. A count that stayed at one across
    /// redeliveries would publish a conflict data point on every retry, which is the amplification
    /// the gate exists to prevent, and nothing in this service could tell the difference.
    /// </remarks>
    [RequiresLocalStack]
    public async Task A_redelivered_message_reports_a_higher_receive_count()
    {
        var queues = await Queues();

        await Publish(queues, OrderEvents.Serialize(OrderEvents.New()));

        var first = Assert.Single(await Receive(queues, 1));

        await ReturnToQueue(queues, first);

        var second = Assert.Single(await Receive(queues, 1));

        Assert.Equal(first.MessageId, second.MessageId);
        Assert.Equal("1", first.Attributes[IncomingMessageMapper.ApproximateReceiveCountAttribute]);
        Assert.Equal("2", second.Attributes[IncomingMessageMapper.ApproximateReceiveCountAttribute]);
    }

    /// <summary>
    /// A message that exhausts its receives moves to the dead-letter queue rather than being
    /// redelivered forever.
    /// </summary>
    /// <remarks>
    /// The receive that moves the message returns nothing, which is why the loop runs one past the
    /// limit. Redrive happens when SQS is asked for a message whose count has already reached
    /// <c>maxReceiveCount</c>, not when the count reaches it.
    /// </remarks>
    [RequiresLocalStack]
    public async Task A_message_that_exhausts_its_receives_moves_to_the_dead_letter_queue()
    {
        var queues = await Queues();

        await Publish(queues, OrderEvents.Serialize(OrderEvents.New()));

        for (var attempt = 0; attempt < SqsQueues.MaxReceiveCount; attempt++)
        {
            var received = Assert.Single(await Receive(queues, 1));

            await ReturnToQueue(queues, received);
        }

        Assert.Empty(await Receive(queues, 1));

        var deadLettered = await fixture.Client.ReceiveMessageAsync(
            new ReceiveMessageRequest
            {
                QueueUrl = queues.DeadLetterQueueUrl,
                MaxNumberOfMessages = 10,
                WaitTimeSeconds = 0,
            },
            TestContext.Current.CancellationToken);

        Assert.Single(deadLettered.Messages);
    }

    private Task<SqsQueues> Queues() => fixture.CreateQueuesAsync(TestContext.Current.CancellationToken);

    private async Task Publish(SqsQueues queues, string body, string? traceParent = null) =>
        await fixture.Client.SendMessageAsync(
            new SendMessageRequest
            {
                QueueUrl = queues.SourceQueueUrl,
                MessageBody = body,
                MessageAttributes = traceParent is null
                    ? []
                    : new Dictionary<string, MessageAttributeValue>(StringComparer.Ordinal)
                    {
                        ["traceparent"] = new() { DataType = "String", StringValue = traceParent },
                    },
            },
            TestContext.Current.CancellationToken);

    private Task<IReadOnlyList<Message>> Receive(SqsQueues queues, int maxRecords) =>
        new EventSourceMapping(fixture.Client, queues)
            .ReceiveAsync(maxRecords, TestContext.Current.CancellationToken);

    private async Task ReturnToQueue(SqsQueues queues, Message message) =>
        await fixture.Client.ChangeMessageVisibilityAsync(
            queues.SourceQueueUrl,
            message.ReceiptHandle,
            0,
            TestContext.Current.CancellationToken);
}
