using System.Globalization;
using Amazon.SQS;
using Amazon.SQS.Model;

namespace ReliableOrders.Local;

/// <summary>
/// The source queue and the dead-letter queue behind it, provisioned the way the CDK provisions the
/// real pair.
/// </summary>
/// <remarks>
/// <para>
/// The names and the redrive setting are copied from <c>MessagingConstruct</c> and
/// <c>EnvironmentConfig</c> rather than read from them. Referencing the CDK project would pull
/// Amazon.CDK.Lib and jsii into a program that runs in a container, to borrow two strings and an
/// integer — the same trade <c>SqsQueues</c> made in the integration tests. What holds the copy
/// honest is <c>LocalStackParityTests</c>, which synthesises the development stack and compares it
/// against the constants here.
/// </para>
/// <para>
/// The visibility timeout is the one value deliberately unlike production. The real queue computes
/// 210 seconds from the Lambda timeout, and a poison message would take five of those to reach the
/// dead-letter queue. Redelivery here is driven by <see cref="EventSourceMapping"/> setting a failed
/// record's visibility back to zero, which is what the real mapping's behaviour amounts to and is
/// immediate, so the timeout is only a safety net for a batch whose invocation never returned.
/// </para>
/// </remarks>
internal sealed class LocalQueues
{
    /// <summary>
    /// How many receives a message survives before redrive moves it.
    /// </summary>
    /// <remarks>
    /// Mirrors <c>EnvironmentConfig.Development.MaxReceiveCount</c>. It is load-bearing rather than
    /// decorative: the poison-message flow is counted against it, and a local stack that
    /// dead-lettered after a different number of attempts would be demonstrating something else.
    /// </remarks>
    internal const int MaxReceiveCount = 5;

    /// <summary>
    /// How long the real queue holds a receive open before answering empty.
    /// </summary>
    /// <remarks>
    /// Mirrors the source queue's <c>ReceiveMessageWaitTime</c>. The event source mapping manages its
    /// own polling and ignores this, but the README's triage commands do not, and short polling
    /// answers empty on a queue that is not.
    /// </remarks>
    internal const int ReceiveWaitSeconds = 20;

    /// <summary>
    /// How long a received message stays invisible. See the type's remarks for why this one differs.
    /// </summary>
    private const int VisibilityTimeoutSeconds = 30;

    private LocalQueues(string sourceQueueUrl, string deadLetterQueueUrl)
    {
        SourceQueueUrl = sourceQueueUrl;
        DeadLetterQueueUrl = deadLetterQueueUrl;
    }

    /// <summary>The queue orders arrive on.</summary>
    internal string SourceQueueUrl { get; }

    /// <summary>The queue holding messages that exhausted their receives.</summary>
    internal string DeadLetterQueueUrl { get; }

    /// <summary>
    /// The queue names for an environment, built the way <c>MessagingConstruct</c> builds them.
    /// </summary>
    /// <param name="environmentName">Names the deployment, and suffixes both queues.</param>
    /// <remarks>
    /// What is worth keeping is that the dead-letter queue is the source queue's name plus
    /// <c>-dlq</c>, because an operator triaging a real queue reads the same relationship.
    /// </remarks>
    internal static (string SourceQueueName, string DeadLetterQueueName) NamesFor(string environmentName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentName);

        var sourceQueueName = $"reliable-orders-{environmentName}";

        return (sourceQueueName, $"{sourceQueueName}-dlq");
    }

    /// <summary>
    /// Creates both queues and wires the redrive policy between them, or adopts the pair a previous
    /// run left behind.
    /// </summary>
    /// <param name="client">A client pointed at the emulator.</param>
    /// <param name="environmentName">Names the pair.</param>
    /// <param name="cancellationToken">Forwarded to each call.</param>
    /// <remarks>
    /// <c>CreateQueue</c> is idempotent for a queue whose attributes match, which is what makes
    /// restarting one service of the stack cheaper than recreating all of it. It is not idempotent
    /// across an attribute change: SQS answers <c>QueueAlreadyExists</c> rather than updating the
    /// queue, so a changed redrive policy needs the emulator's volume gone — which is what
    /// <c>docker compose down</c> does, and what the README says to do.
    /// </remarks>
    internal static async Task<LocalQueues> CreateAsync(
        IAmazonSQS client,
        string environmentName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);

        var names = NamesFor(environmentName);

        // The dead-letter queue first, because the source queue's redrive policy names its ARN and a
        // queue has no ARN before it exists. The CDK has the same ordering problem and solves it the
        // other way, by composing the ARN from the account and Region so neither queue references the
        // other. Nothing local needs that, and composing an ARN by hand here would be asserting the
        // emulator's ARN format rather than reading it.
        var deadLetterQueue = await client.CreateQueueAsync(
            new CreateQueueRequest { QueueName = names.DeadLetterQueueName },
            cancellationToken);

        var attributes = await client.GetQueueAttributesAsync(
            new GetQueueAttributesRequest
            {
                QueueUrl = deadLetterQueue.QueueUrl,
                AttributeNames = [QueueAttributeName.QueueArn.Value],
            },
            cancellationToken);

        var sourceQueue = await client.CreateQueueAsync(
            new CreateQueueRequest
            {
                QueueName = names.SourceQueueName,
                Attributes = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [QueueAttributeName.VisibilityTimeout.Value] =
                        VisibilityTimeoutSeconds.ToString(CultureInfo.InvariantCulture),
                    [QueueAttributeName.ReceiveMessageWaitTimeSeconds.Value] =
                        ReceiveWaitSeconds.ToString(CultureInfo.InvariantCulture),
                    [QueueAttributeName.RedrivePolicy.Value] = RedrivePolicy(attributes.QueueARN),
                },
            },
            cancellationToken);

        return new LocalQueues(sourceQueue.QueueUrl, deadLetterQueue.QueueUrl);
    }

    /// <summary>
    /// Finds the pair a previous <c>provision</c> created, without creating anything.
    /// </summary>
    /// <remarks>
    /// The mapping resolves rather than creates, so a stack whose provisioning step was skipped fails
    /// naming the queue instead of quietly polling a second, empty queue of its own making.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Either queue is absent.</exception>
    internal static async Task<LocalQueues> ResolveAsync(
        IAmazonSQS client,
        string environmentName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);

        var names = NamesFor(environmentName);

        return new LocalQueues(
            await UrlOfAsync(client, names.SourceQueueName, cancellationToken),
            await UrlOfAsync(client, names.DeadLetterQueueName, cancellationToken));
    }

    private static async Task<string> UrlOfAsync(
        IAmazonSQS client,
        string queueName,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await client.GetQueueUrlAsync(queueName, cancellationToken);

            return response.QueueUrl;
        }
        catch (QueueDoesNotExistException absent)
        {
            throw new InvalidOperationException(
                $"There is no queue named '{queueName}'. The provisioning step creates it; run "
                + "`docker compose up` rather than starting this service on its own.",
                absent);
        }
    }

    /// <remarks>
    /// <c>maxReceiveCount</c> is written as a string because that is what SQS accepts in this
    /// document, whatever its name suggests.
    /// </remarks>
    private static string RedrivePolicy(string deadLetterQueueArn) =>
        $$"""
          {"deadLetterTargetArn":"{{deadLetterQueueArn}}","maxReceiveCount":"{{MaxReceiveCount}}"}
          """;
}
