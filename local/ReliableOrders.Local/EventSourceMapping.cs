using System.Diagnostics;
using Amazon.SQS;
using Amazon.SQS.Model;
using SqsMessage = Amazon.SQS.Model.Message;

namespace ReliableOrders.Local;

/// <summary>
/// Stands in for the Lambda event source mapping: reads a batch off the queue, invokes the function
/// with it, and acts on the response.
/// </summary>
/// <remarks>
/// <para>
/// The mapping is the one piece of the deployed path this stack cannot run, because it is AWS-side.
/// What it does is small and documented, and it is what gives a batch response its consequences — a
/// record named in <c>batchItemFailures</c> is left on the queue and redelivered, and every other
/// record in the batch is deleted. Without something applying that rule, the stack would show the
/// shape of a response and nothing about what it causes, which is the half that matters.
/// </para>
/// <para>
/// It is a stand-in and not a claim. Batch size and the batching window are modelled, because what a
/// batch is decides what a batch response demonstrates; concurrency, scaling, and the mapping's own
/// failure and retry behaviour are not. Story 6.3 is where the real thing is exercised, against a
/// real account.
/// </para>
/// </remarks>
internal sealed class EventSourceMapping
{
    /// <summary>
    /// How long a poll waits for a message before answering empty.
    /// </summary>
    /// <remarks>
    /// The queue's own long-poll setting, restated on the request. SQS takes the shorter of the two,
    /// and stating it here means the loop's pace does not change if someone adopts a queue that was
    /// created without it.
    /// </remarks>
    private const int PollWaitSeconds = LocalQueues.ReceiveWaitSeconds;

    /// <summary>
    /// How long after a failed poll the next one is attempted.
    /// </summary>
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long a batch is given to fill once its first message has arrived.
    /// </summary>
    /// <remarks>
    /// Mirrors <c>EnvironmentConfig.Development.BatchWindowSeconds</c>, which reaches the deployed
    /// mapping as <c>MaximumBatchingWindowInSeconds</c>. <c>LocalStackParityTests</c> holds the two in
    /// step, because a local stack that batched over a different window would show a different batch.
    /// </remarks>
    internal const int BatchWindowSeconds = 1;

    /// <inheritdoc cref="BatchWindowSeconds"/>
    private static readonly TimeSpan BatchWindow = TimeSpan.FromSeconds(BatchWindowSeconds);

    /// <summary>
    /// How long a poll inside the batching window waits.
    /// </summary>
    /// <remarks>
    /// The shortest wait SQS accepts above none at all. Zero would spin through the window issuing
    /// receives as fast as the network allows, for messages that have not arrived yet.
    /// </remarks>
    private const int GatherWaitSeconds = 1;

    private readonly IAmazonSQS _client;
    private readonly FunctionInvoker _invoker;
    private readonly LocalQueues _queues;
    private readonly string _queueArn;
    private readonly string _region;
    private readonly int _batchSize;

    private EventSourceMapping(
        IAmazonSQS client,
        FunctionInvoker invoker,
        LocalQueues queues,
        string queueArn,
        string region,
        int batchSize)
    {
        _client = client;
        _invoker = invoker;
        _queues = queues;
        _queueArn = queueArn;
        _region = region;
        _batchSize = batchSize;
    }

    /// <summary>
    /// Binds a mapping to a queue, reading the ARN every record it delivers will carry.
    /// </summary>
    /// <param name="client">A client pointed at the emulator.</param>
    /// <param name="invoker">Reaches the function.</param>
    /// <param name="queues">The pair this mapping polls and dead-letters through.</param>
    /// <param name="configuration">Supplies the region and the batch size.</param>
    /// <param name="cancellationToken">Cancels the attribute read.</param>
    internal static async Task<EventSourceMapping> ForAsync(
        IAmazonSQS client,
        FunctionInvoker invoker,
        LocalQueues queues,
        LocalConfiguration configuration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(queues);
        ArgumentNullException.ThrowIfNull(configuration);

        // Read rather than composed. The emulator's ARNs are its own, and a hand-built one would put a
        // string in every record that names a queue nothing can resolve.
        var attributes = await client.GetQueueAttributesAsync(
            new GetQueueAttributesRequest
            {
                QueueUrl = queues.SourceQueueUrl,
                AttributeNames = [QueueAttributeName.QueueArn.Value],
            },
            cancellationToken);

        return new EventSourceMapping(
            client,
            invoker,
            queues,
            attributes.QueueARN,
            configuration.Region,
            configuration.BatchSize);
    }

    /// <summary>
    /// Polls until cancelled, invoking the function with every batch it receives.
    /// </summary>
    /// <param name="cancellationToken">Ends the loop, which is what a stop signal cancels.</param>
    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        Log.Line($"Polling {_queueArn} in batches of up to {_batchSize}.");

        while (!cancellationToken.IsCancellationRequested)
        {
            // Every iteration stands on its own. A long poll interrupted by a paused container, an
            // emulator restarted underneath this one, a socket closed mid-receive, an invocation that
            // ran past the invoker's timeout — none of them are reasons to stop polling, and the real
            // mapping survives all of them. Left uncaught, the stack would go quiet mid-demonstration
            // and the reason would be one line above the prompt.
            //
            // The filter asks the token rather than the type, for the reason Stopping gives: a
            // timeout and a stop signal throw the same exception, and only one of them means stop.
            try
            {
                await PollAsync(cancellationToken);
            }
            catch (Exception interrupted) when (!Stopping.Requested(interrupted, cancellationToken))
            {
                Log.Line($"The poll failed and will be retried: {interrupted.Message}");

                // Paced, because the failure that repeats is the interesting one and a hot loop
                // against a dead emulator writes thousands of lines before anyone reads the first.
                await Task.Delay(RetryDelay, cancellationToken);
            }
        }
    }

    /// <summary>
    /// One receive, one invocation, and the response applied.
    /// </summary>
    private async Task PollAsync(CancellationToken cancellationToken)
    {
        var batch = await ReceiveAsync(cancellationToken);

        if (batch.Count == 0)
        {
            return;
        }

        var failures = await _invoker.InvokeAsync(ToBatch(batch), cancellationToken);

        // Null is an invocation that never produced a response. Nothing is deleted and nothing is
        // made visible early: the whole batch waits out its visibility timeout and is redelivered,
        // which is what the real mapping does with a failed invocation.
        if (failures is null)
        {
            return;
        }

        await ApplyAsync(batch, failures, cancellationToken);
    }

    /// <summary>
    /// Reads up to the batch size, as one batch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A long poll for the first message, and then a batching window to fill the rest. One receive is
    /// not enough: SQS is entitled to return fewer messages than are available and does, so a batch
    /// assembled from a single call arrives as several batches of one — which is the shape a mixed
    /// batch is least able to demonstrate anything in.
    /// </para>
    /// <para>
    /// The window is the deployed mapping's own, and it is the reason this is faithful rather than
    /// merely convenient: a real event source mapping waits <c>MaximumBatchingWindowInSeconds</c> for
    /// a batch to fill before invoking. The gather runs for the whole window rather than stopping at
    /// the first empty response, because that is what waiting means — two events published a few
    /// hundred milliseconds apart belong to one batch.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<SqsMessage>> ReceiveAsync(CancellationToken cancellationToken)
    {
        var first = await ReceiveOnceAsync(_batchSize, PollWaitSeconds, cancellationToken);

        if (first.Count == 0)
        {
            return [];
        }

        var batch = new List<SqsMessage>(first);
        var window = Stopwatch.StartNew();

        while (batch.Count < _batchSize && window.Elapsed < BatchWindow)
        {
            batch.AddRange(
                await ReceiveOnceAsync(_batchSize - batch.Count, GatherWaitSeconds, cancellationToken));
        }

        return batch;
    }

    /// <summary>
    /// One receive.
    /// </summary>
    /// <param name="maximum">The most messages this call may return.</param>
    /// <param name="waitSeconds">How long the call waits for one to turn up.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    private async Task<IReadOnlyList<SqsMessage>> ReceiveOnceAsync(
        int maximum,
        int waitSeconds,
        CancellationToken cancellationToken)
    {
        var response = await _client.ReceiveMessageAsync(
            new ReceiveMessageRequest
            {
                QueueUrl = _queues.SourceQueueUrl,
                MaxNumberOfMessages = maximum,
                WaitTimeSeconds = waitSeconds,

                // Both requested explicitly. SQS returns neither by default, and the receive count is
                // what the first-receipt metric gate reads, so a batch built without it would exercise
                // the mapper's fallback rather than the value SQS sent.
                MessageSystemAttributeNames = ["All"],
                MessageAttributeNames = ["All"],
            },
            cancellationToken);

        return response.Messages ?? [];
    }

    /// <summary>
    /// Applies the response: the records it names stay on the queue, and the rest are deleted.
    /// </summary>
    /// <remarks>
    /// Visibility is reset to zero rather than left to expire, so the next receive sees the record
    /// immediately. That changes when a redelivery happens and nothing about whether it does, and it
    /// is what makes the poison-message flow take seconds rather than the five visibility timeouts a
    /// real deployment spends on it.
    /// </remarks>
    private async Task ApplyAsync(
        IReadOnlyList<SqsMessage> batch,
        IReadOnlyCollection<string> failures,
        CancellationToken cancellationToken)
    {
        var failed = failures.ToHashSet(StringComparer.Ordinal);
        var deleted = 0;
        var returned = 0;

        foreach (var message in batch)
        {
            var redeliver = failed.Contains(message.MessageId);

            // Each record on its own, because a receipt handle can go stale while the batch is being
            // applied: an invocation that outran the visibility timeout makes its records visible
            // again mid-flight, and the call against the first stale handle would otherwise abandon
            // every record after it — leaving records the function processed on the queue, to be
            // redelivered with their receive counts climbing toward the dead-letter queue. Which ones
            // survived would depend on the order they came off the queue in.
            try
            {
                if (redeliver)
                {
                    await _client.ChangeMessageVisibilityAsync(
                        _queues.SourceQueueUrl,
                        message.ReceiptHandle,
                        0,
                        cancellationToken);

                    returned++;
                }
                else
                {
                    await _client.DeleteMessageAsync(
                        _queues.SourceQueueUrl,
                        message.ReceiptHandle,
                        cancellationToken);

                    deleted++;
                }
            }
            catch (Exception unapplied) when (!Stopping.Requested(unapplied, cancellationToken))
            {
                Log.Line(
                    $"Message {message.MessageId} could not be "
                    + $"{(redeliver ? "returned for redelivery" : "deleted")}, so it stays on the queue "
                    + $"until its visibility expires: {unapplied.Message}");
            }
        }

        // Counted from what the calls above actually did, not from the response. The two differ
        // whenever one of them fails, and this is the line the README tells a reader to check a
        // partial batch response against — a number derived from the response would report the
        // intention and read as the outcome.
        Log.Line($"Batch of {batch.Count}: {deleted} deleted, {returned} returned for redelivery.");

        // An identifier naming no record in this batch is a defect in the handler, not in the queue,
        // and it is an expensive one: real Lambda redelivers the entire batch rather than the record
        // it cannot match. BatchItemFailures refuses to emit one, so seeing it here means that guard
        // has been bypassed.
        var unmatched = failed
            .Except(batch.Select(message => message.MessageId), StringComparer.Ordinal)
            .ToArray();

        if (unmatched.Length > 0)
        {
            Log.Line(
                $"The response named {unmatched.Length} identifier(s) this batch does not carry: "
                + $"{string.Join(", ", unmatched)}. Deployed, that redelivers the whole batch.");
        }
    }

    /// <summary>
    /// Builds the event the runtime would deserialise from a batch of received messages.
    /// </summary>
    private SqsEventPayload ToBatch(IReadOnlyList<SqsMessage> messages) =>
        new() { Records = [.. messages.Select(ToRecord)] };

    private SqsRecordPayload ToRecord(SqsMessage message) => new()
    {
        MessageId = message.MessageId,
        ReceiptHandle = message.ReceiptHandle,
        Body = message.Body,
        Attributes = message.Attributes is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(message.Attributes, StringComparer.Ordinal),
        MessageAttributes = message.MessageAttributes is null
            ? new Dictionary<string, SqsMessageAttributePayload>(StringComparer.Ordinal)
            : message.MessageAttributes.ToDictionary(
                attribute => attribute.Key,
                attribute => new SqsMessageAttributePayload
                {
                    StringValue = attribute.Value.StringValue,
                    DataType = attribute.Value.DataType,
                },
                StringComparer.Ordinal),
        EventSource = "aws:sqs",
        EventSourceArn = _queueArn,
        AwsRegion = _region,
    };
}
