using Amazon.Lambda.SQSEvents;
using Amazon.SQS;
using Amazon.SQS.Model;
using SqsMessage = Amazon.SQS.Model.Message;

namespace ReliableOrders.IntegrationTests;

/// <summary>
/// Stands in for the Lambda event source mapping: reads a batch off the queue, and acts on the
/// response the handler returns.
/// </summary>
/// <remarks>
/// <para>
/// The mapping is the piece these tests cannot run, because it is AWS-side. What it does is small and
/// documented, and it is what gives a batch response its consequences — a record named in
/// <c>batchItemFailures</c> is left on the queue and redelivered, and every other record in the batch
/// is deleted. Without something applying that rule, a test can assert the shape of a response and
/// nothing about what it causes, which is the half that matters.
/// </para>
/// <para>
/// It is a stand-in and not a claim. Batch window, concurrency, and what the mapping does when the
/// invocation itself fails are not modelled here, and Story 6.3 is where the real thing is exercised.
/// </para>
/// </remarks>
internal sealed class EventSourceMapping
{
    /// <summary>
    /// How many empty receives in a row are taken to mean the queue has nothing left.
    /// </summary>
    /// <remarks>
    /// More than one because SQS may return fewer messages than are available, and this emulator is
    /// entitled to the same latitude. Two consecutive empty responses against a single-process
    /// emulator on the same machine is the point at which waiting longer stops being diagnosis and
    /// starts being a slower failure.
    /// </remarks>
    private const int EmptyReceivesBeforeGivingUp = 2;

    /// <summary>
    /// How long a poll after the first waits for a message to turn up.
    /// </summary>
    /// <remarks>
    /// The first poll returns whatever is already visible, which is the common case and the one worth
    /// keeping instant. Every poll after it waits, because the alternative is a race the caller
    /// cannot see: a send and a receive microseconds apart against an emulator that has not made the
    /// message visible yet returns empty twice, and a test asserting on a count fails naming the
    /// count rather than the timing. Two consecutive empty responses end the gather, so the waiting
    /// costs a second on an empty queue and two where a partial batch is followed by two empties.
    /// </remarks>
    private const int RetryWaitSeconds = 1;

    private readonly IAmazonSQS _client;
    private readonly SqsQueues _queues;

    internal EventSourceMapping(IAmazonSQS client, SqsQueues queues)
    {
        _client = client;
        _queues = queues;
    }

    /// <summary>
    /// Reads up to <paramref name="maxRecords"/> messages, as one batch.
    /// </summary>
    /// <remarks>
    /// Polls until it has that many or the queue answers empty twice, rather than taking one receive
    /// as the whole batch. A test publishing three messages and receiving two would otherwise assert
    /// against a batch missing a record, and the failure would name the wrong thing. The first poll
    /// is immediate and the rest wait, for the reason given on <see cref="RetryWaitSeconds"/>.
    /// </remarks>
    internal async Task<IReadOnlyList<SqsMessage>> ReceiveAsync(
        int maxRecords,
        CancellationToken cancellationToken)
    {
        var received = new List<SqsMessage>(maxRecords);
        var empties = 0;
        var polls = 0;

        while (received.Count < maxRecords && empties < EmptyReceivesBeforeGivingUp)
        {
            var response = await _client.ReceiveMessageAsync(
                new ReceiveMessageRequest
                {
                    QueueUrl = _queues.SourceQueueUrl,
                    MaxNumberOfMessages = maxRecords - received.Count,
                    WaitTimeSeconds = polls++ == 0 ? 0 : RetryWaitSeconds,

                    // Both requested explicitly. SQS returns neither by default, and the receive
                    // count is what the first-receipt metric gate reads, so a batch built without it
                    // would exercise the mapper's fallback rather than the value SQS sent.
                    MessageSystemAttributeNames = ["All"],
                    MessageAttributeNames = ["All"],
                },
                cancellationToken);

            var messages = response.Messages ?? [];

            if (messages.Count == 0)
            {
                empties++;

                continue;
            }

            empties = 0;

            received.AddRange(messages);
        }

        return received;
    }

    /// <summary>
    /// Applies the response: the records it names stay on the queue, and the rest are deleted.
    /// </summary>
    /// <remarks>
    /// Visibility is reset to zero rather than left to expire, so the next receive sees the record
    /// immediately. That changes when redelivery happens and nothing about whether it does, which is
    /// what keeps these tests off the clock.
    /// </remarks>
    internal async Task ApplyAsync(
        IReadOnlyList<SqsMessage> batch,
        SQSBatchResponse response,
        CancellationToken cancellationToken)
    {
        var failed = response.BatchItemFailures
            .Select(failure => failure.ItemIdentifier)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var message in batch)
        {
            if (failed.Contains(message.MessageId))
            {
                await _client.ChangeMessageVisibilityAsync(
                    _queues.SourceQueueUrl,
                    message.ReceiptHandle,
                    0,
                    cancellationToken);

                continue;
            }

            await _client.DeleteMessageAsync(
                _queues.SourceQueueUrl,
                message.ReceiptHandle,
                cancellationToken);
        }
    }

    /// <summary>
    /// Builds the event the runtime would deserialise from a batch of received messages.
    /// </summary>
    internal static SQSEvent ToBatch(IReadOnlyList<SqsMessage> messages) =>
        new() { Records = [.. messages.Select(ToRecord)] };

    /// <remarks>
    /// Only the fields anything downstream reads are carried across. Adding the rest would suggest
    /// the mapper depends on them, and it is the mapper's own tests that pin what it reads.
    /// </remarks>
    private static SQSEvent.SQSMessage ToRecord(SqsMessage message) => new()
    {
        MessageId = message.MessageId,
        ReceiptHandle = message.ReceiptHandle,
        Body = message.Body,
        Attributes = message.Attributes is null
            ? []
            : new Dictionary<string, string>(message.Attributes, StringComparer.Ordinal),
        MessageAttributes = message.MessageAttributes is null
            ? []
            : message.MessageAttributes.ToDictionary(
                attribute => attribute.Key,
                attribute => new SQSEvent.MessageAttribute
                {
                    DataType = attribute.Value.DataType,
                    StringValue = attribute.Value.StringValue,
                },
                StringComparer.Ordinal),
        EventSource = "aws:sqs",
        AwsRegion = LocalStackFixture.Region,
    };
}
