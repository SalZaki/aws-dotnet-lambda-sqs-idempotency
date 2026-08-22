using System.Globalization;
using System.Text.Json;
using Amazon.CloudWatch.Model;
using Amazon.CloudWatchLogs.Model;
using Amazon.DynamoDBv2.Model;
using Amazon.SQS.Model;
using ReliableOrders.Core.Contracts;
using ReliableOrders.Core.Observability;
using ReliableOrders.Core.Persistence;

namespace ReliableOrders.EndToEndTests;

/// <summary>
/// What a scenario asks the account after it has sent something.
/// </summary>
/// <remarks>
/// <para>
/// Separated from <see cref="Deployment"/>, which knows what was deployed and holds the clients. This
/// knows what a question looks like — and every one of them is a poll, because each of these services
/// becomes consistent about the answer some time after the function has finished.
/// </para>
/// <para>
/// The deadlines differ by an order of magnitude and each is named where it is used. A table read is
/// seconds behind an invocation; a metric is a minute or two behind the log line that carried it; and
/// a dead-lettered message is behind the whole redrive policy, which is receives multiplied by the
/// visibility timeout.
/// </para>
/// </remarks>
public static class DeploymentQueries
{
    /// <summary>
    /// How long a write takes to be readable, generously.
    /// </summary>
    internal static readonly TimeSpan WriteVisible = TimeSpan.FromMinutes(2);

    /// <summary>How long CloudWatch takes to admit a metric an EMF line published.</summary>
    /// <remarks>
    /// Longer than it usually is. The alternative to waiting is a suite that fails on propagation
    /// rather than on behaviour, and a failure nobody trusts is one people rerun until it is green.
    /// </remarks>
    internal static readonly TimeSpan MetricVisible = TimeSpan.FromMinutes(6);

    /// <summary>
    /// Sends one event, and returns the identifier SQS gave the message.
    /// </summary>
    internal static async Task<string> Send(this Deployment deployment, OrderCreatedV1 orderEvent)
    {
        var sent = await deployment.Sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = deployment.SourceQueueUrl,
            MessageBody = EndToEndEvents.Serialize(orderEvent),
        }).ConfigureAwait(false);

        return sent.MessageId;
    }

    /// <summary>Sends a body nothing will parse.</summary>
    internal static async Task<string> SendRaw(this Deployment deployment, string body)
    {
        var sent = await deployment.Sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = deployment.SourceQueueUrl,
            MessageBody = body,
        }).ConfigureAwait(false);

        return sent.MessageId;
    }

    /// <summary>Sends several bodies as one batch, so the function sees them in one invocation.</summary>
    /// <remarks>
    /// One batch is the point of the case that uses it: a partial batch response is only observable
    /// where the records arrived together, and sending them separately would demonstrate nothing
    /// about it.
    /// </remarks>
    internal static async Task SendBatch(this Deployment deployment, IReadOnlyList<string> bodies)
    {
        _ = await deployment.Sqs.SendMessageBatchAsync(new SendMessageBatchRequest
        {
            QueueUrl = deployment.SourceQueueUrl,
            Entries =
            [
                .. bodies.Select((body, index) => new SendMessageBatchRequestEntry
                {
                    Id = index.ToString(CultureInfo.InvariantCulture), MessageBody = body,
                }),
            ],
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// The stored order, once it is there.
    /// </summary>
    internal static Task<Dictionary<string, AttributeValue>?> Order(
        this Deployment deployment,
        string orderId,
        TimeSpan? within = null) =>
        Deployment.Until(
            () => Item(deployment, deployment.OrdersTableName, OrderTableSchema.PartitionKey, orderId),
            within ?? WriteVisible);

    /// <summary>The stored idempotency record, once it is there.</summary>
    internal static Task<Dictionary<string, AttributeValue>?> IdempotencyRecord(
        this Deployment deployment,
        Guid eventId,
        TimeSpan? within = null) =>
        Deployment.Until(
            () => Item(
                deployment,
                deployment.IdempotencyTableName,
                IdempotencyTableSchema.PartitionKey,
                eventId.ToString()),
            within ?? WriteVisible);

    /// <summary>
    /// The dead-lettered message carrying a given identifier, once the redrive policy gives up on it.
    /// </summary>
    /// <param name="deployment">What to ask.</param>
    /// <param name="contains">Something only the message being waited for carries.</param>
    /// <param name="within">How long to wait, which the caller computes from the queue.</param>
    /// <remarks>
    /// Received rather than counted, because a queue's approximate depth is approximate and the
    /// assertion is about one message. What is received is left on the queue: a scenario that deleted
    /// it would hide the evidence from whoever reads the run afterwards.
    /// </remarks>
    internal static Task<Message?> DeadLettered(
        this Deployment deployment,
        string contains,
        TimeSpan within) =>
        Deployment.Until(
            async () =>
            {
                var received = await deployment.Sqs.ReceiveMessageAsync(new ReceiveMessageRequest
                {
                    QueueUrl = deployment.DeadLetterQueueUrl,
                    MaxNumberOfMessages = 10,
                    WaitTimeSeconds = 10,
                    VisibilityTimeout = 1,

                    // Asked for, because SQS returns system attributes only where they are, and the
                    // SDK leaves the collection null rather than empty when they are not. A caller
                    // reading the receive count off the message would otherwise fail with a null
                    // reference, and only after the whole redrive wait had been paid for.
                    MessageSystemAttributeNames = ["ApproximateReceiveCount"],
                }).ConfigureAwait(false);

                return received.Messages?.Find(message =>
                    message.Body.Contains(contains, StringComparison.Ordinal));
            },
            within,
            TimeSpan.FromSeconds(5));

    /// <summary>
    /// How long the queue's own settings say a message takes to reach the dead-letter queue.
    /// </summary>
    /// <remarks>
    /// Read from the deployed queue rather than computed from the configuration that built it. The
    /// configuration is what the CDK tests assert on; what a message gets is whatever SQS is holding,
    /// and a test waiting on the wrong one of those two fails as a timeout with nothing to say. The
    /// first receive happens at once, so it is the receives after it that cost a visibility timeout
    /// each.
    /// </remarks>
    internal static async Task<TimeSpan> DeadLetterDeadline(this Deployment deployment)
    {
        var (visibility, receives) = await deployment.Redrive().ConfigureAwait(false);

        // Half a visibility timeout of slack, and a minute for the last invocation to run and for the
        // move to be visible on the other queue.
        return (visibility * (receives - 1)) + (visibility / 2) + TimeSpan.FromMinutes(1);
    }

    /// <summary>
    /// The redrive policy as the queue holds it: how long a failed receive hides the message, and how
    /// many receives it survives.
    /// </summary>
    /// <remarks>
    /// Read from the deployed queue rather than computed from the configuration that built it. The
    /// configuration is what the CDK tests assert on; what a message gets is whatever SQS is holding,
    /// and a test waiting on or asserting against the wrong one of those two says nothing useful.
    /// </remarks>
    internal static async Task<(TimeSpan Visibility, int Receives)> Redrive(this Deployment deployment)
    {
        var attributes = await deployment.Sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = deployment.SourceQueueUrl,
            AttributeNames = ["VisibilityTimeout", "RedrivePolicy"],
        }).ConfigureAwait(false);

        return (
            TimeSpan.FromSeconds(attributes.VisibilityTimeout),
            MaxReceiveCount(attributes.Attributes["RedrivePolicy"]));
    }

    /// <summary>
    /// The log lines matching a pattern, once at least <paramref name="atLeast"/> of them are there.
    /// </summary>
    /// <param name="deployment">What to ask.</param>
    /// <param name="pattern">A CloudWatch Logs filter pattern.</param>
    /// <param name="atLeast">How many lines the caller is waiting for.</param>
    /// <param name="within">How long to wait.</param>
    /// <remarks>
    /// Waiting for a count rather than for the first match, because several cases assert that
    /// something happened once. A read returning as soon as one line existed would pass before a
    /// second could have arrived, which is the assertion inverted.
    /// </remarks>
    internal static async Task<IReadOnlyList<FilteredLogEvent>> LogLines(
        this Deployment deployment,
        string pattern,
        int atLeast,
        TimeSpan within)
    {
        var found = await Deployment.Until(
            async () =>
            {
                var events = await Filter(deployment, pattern).ConfigureAwait(false);

                return events.Count >= atLeast ? events : null;
            },
            within).ConfigureAwait(false);

        return found ?? await Filter(deployment, pattern).ConfigureAwait(false);
    }

    /// <summary>
    /// The sum of a metric over the run, once it is greater than zero.
    /// </summary>
    /// <param name="deployment">What to ask.</param>
    /// <param name="metricName">One of <see cref="MetricNames"/>.</param>
    /// <param name="since">Where the window starts, which is before the scenario sent anything.</param>
    /// <remarks>
    /// Dimensioned by service and environment, which is what the publisher writes and what the
    /// dashboard reads. A query without them would sum every environment in the account and pass on a
    /// metric this run never published.
    /// </remarks>
    /// <param name="settle">
    /// How long to keep watching after the first non-zero read, for a caller asserting on the value
    /// rather than on its existence. A sum read the moment it stops being zero is a sum that later
    /// points can still be added to.
    /// </param>
    internal static async Task<double> MetricSum(
        this Deployment deployment,
        string metricName,
        DateTimeOffset since,
        TimeSpan? settle = null)
    {
        // Boxed, because the poll waits for a reference and a sum is a number. Reading it back out
        // costs less than a second poll written for value types.
        var sum = await Deployment.Until(
            async () =>
            {
                var value = await Sum(deployment, metricName, since).ConfigureAwait(false);

                return value > 0 ? (object)value : null;
            },
            MetricVisible,
            TimeSpan.FromSeconds(15)).ConfigureAwait(false);

        if (sum is not double found)
        {
            return await Sum(deployment, metricName, since).ConfigureAwait(false);
        }

        if (settle is not { } window)
        {
            return found;
        }

        await Task.Delay(window).ConfigureAwait(false);

        return await Sum(deployment, metricName, since).ConfigureAwait(false);
    }

    /// <summary>One item, or null while it is not there.</summary>
    private static async Task<Dictionary<string, AttributeValue>?> Item(
        Deployment deployment,
        string tableName,
        string key,
        string value)
    {
        var read = await deployment.DynamoDb.GetItemAsync(new GetItemRequest
        {
            TableName = tableName,
            Key = new Dictionary<string, AttributeValue> { [key] = new() { S = value } },

            // The write is a transaction and this read is what asserts it happened. An eventually
            // consistent read can answer from a replica that has not caught up, which would make a
            // correct stack look like a slow one.
            ConsistentRead = true,
        }).ConfigureAwait(false);

        return read.IsItemSet ? read.Item : null;
    }

    private static async Task<IReadOnlyList<FilteredLogEvent>> Filter(Deployment deployment, string pattern)
    {
        var filtered = await deployment.Logs.FilterLogEventsAsync(new FilterLogEventsRequest
        {
            LogGroupName = deployment.LogGroupName,
            FilterPattern = pattern,
        }).ConfigureAwait(false);

        return filtered.Events ?? [];
    }

    private static async Task<double> Sum(Deployment deployment, string metricName, DateTimeOffset since)
    {
        var result = await deployment.Metrics.GetMetricDataAsync(new GetMetricDataRequest
        {
            StartTime = since.UtcDateTime,
            EndTime = DateTimeOffset.UtcNow.UtcDateTime.AddMinutes(1),
            MetricDataQueries =
            [
                new MetricDataQuery
                {
                    Id = "sum",
                    MetricStat = new MetricStat
                    {
                        Period = 60,
                        Stat = "Sum",
                        Metric = new Metric
                        {
                            Namespace = MetricsNamespace,
                            MetricName = metricName,
                            Dimensions =
                            [
                                new Dimension { Name = LogFields.Service, Value = ServiceName },
                                new Dimension { Name = LogFields.Environment, Value = Deployment.EnvironmentName },
                            ],
                        },
                    },
                },
            ],
        }).ConfigureAwait(false);

        return result.MetricDataResults?.SelectMany(data => data.Values ?? []).Sum() ?? 0;
    }

    /// <summary>The receive count out of a redrive policy, which SQS returns as a JSON string.</summary>
    private static int MaxReceiveCount(string redrivePolicy)
    {
        using var policy = JsonDocument.Parse(redrivePolicy);

        return policy.RootElement.GetProperty("maxReceiveCount").GetInt32();
    }

    /// <summary>
    /// What the deployed function publishes under, copied from the construct that deploys it.
    /// </summary>
    /// <remarks>
    /// Copied rather than referenced: taking these from the CDK project would pull Amazon.CDK.Lib and
    /// jsii into a suite whose whole job is to talk to an account. <c>PublishedMetricsTests</c> in the
    /// CDK suite holds them in step with the construct's own.
    /// </remarks>
    public const string MetricsNamespace = "ReliableOrders";

    /// <summary>The service dimension every metric carries.</summary>
    public const string ServiceName = "reliable-orders";
}
