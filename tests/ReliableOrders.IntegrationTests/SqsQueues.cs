using System.Globalization;
using Amazon.SQS;
using Amazon.SQS.Model;

namespace ReliableOrders.IntegrationTests;

/// <summary>
/// A source queue and the dead-letter queue behind it, provisioned the way the CDK provisions the
/// real pair.
/// </summary>
/// <remarks>
/// <para>
/// The settings that carry behaviour are the ones copied: redrive to a dead-letter queue after a
/// fixed number of receives, and a visibility timeout long enough that a received message is not
/// handed to a second reader mid-test. Encryption, retention, and the redrive allow policy are left
/// off — they are production settings with no local effect, and the CDK assertions are their
/// authority.
/// </para>
/// <para>
/// The names come from <see cref="MessagingConstructNaming"/> below rather than from the CDK project.
/// Referencing it would pull Amazon.CDK.Lib and jsii into a test assembly that starts containers, to
/// borrow two strings and an integer.
/// </para>
/// </remarks>
internal sealed class SqsQueues
{
    /// <summary>
    /// How many receives a message survives before redrive moves it.
    /// </summary>
    /// <remarks>
    /// Mirrors <c>EnvironmentConfig.Development.MaxReceiveCount</c>. The number matters here because
    /// the poison-message test counts receives against it, and a local demonstration that dead-letters
    /// after a different number of attempts than production would be demonstrating something else.
    /// The CDK assertion that it is at least five is the rule; this is a copy of the value.
    /// </remarks>
    internal const int MaxReceiveCount = 5;

    /// <summary>
    /// How long a received message stays invisible.
    /// </summary>
    /// <remarks>
    /// Thirty seconds, and no test waits for it to elapse. Redelivery is driven by setting a
    /// message's visibility back to zero, which is what the event source mapping's own behaviour
    /// amounts to and is deterministic where sleeping is not. The production value comes from the
    /// visibility timeout formula and is asserted in the CDK tests; nothing local depends on it.
    /// </remarks>
    private const int VisibilityTimeoutSeconds = 30;

    private SqsQueues(string sourceQueueUrl, string deadLetterQueueUrl)
    {
        SourceQueueUrl = sourceQueueUrl;
        DeadLetterQueueUrl = deadLetterQueueUrl;
    }

    /// <summary>The queue orders arrive on.</summary>
    internal string SourceQueueUrl { get; }

    /// <summary>The queue holding messages that exhausted their receives.</summary>
    internal string DeadLetterQueueUrl { get; }

    /// <summary>
    /// Creates both queues and wires the redrive policy between them.
    /// </summary>
    /// <param name="client">A client pointed at the emulator.</param>
    /// <param name="suffix">Distinguishes this pair from every other pair in the run.</param>
    /// <param name="cancellationToken">Forwarded to each call.</param>
    internal static async Task<SqsQueues> CreateAsync(
        IAmazonSQS client,
        string suffix,
        CancellationToken cancellationToken)
    {
        var names = MessagingConstructNaming.For(suffix);

        // The dead-letter queue first, because the source queue's redrive policy names its ARN and a
        // queue has no ARN before it exists. The CDK has the same ordering problem and solves it the
        // other way, by composing the ARN from the account and Region so that neither queue has to
        // reference the other. Nothing local needs that, and composing an ARN by hand here would be
        // asserting the emulator's ARN format rather than reading it.
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

                    // Short polling, where the real queue long-polls for twenty seconds. A test that
                    // asserts a queue is empty would otherwise wait the full twenty to be told so.
                    // Nothing is missed by it: the emulator is one process on the same machine, so a
                    // message that has been sent is visible to the next receive.
                    [QueueAttributeName.ReceiveMessageWaitTimeSeconds.Value] = "0",

                    [QueueAttributeName.RedrivePolicy.Value] = RedrivePolicy(attributes.QueueARN),
                },
            },
            cancellationToken);

        return new SqsQueues(sourceQueue.QueueUrl, deadLetterQueue.QueueUrl);
    }

    /// <remarks>
    /// <c>maxReceiveCount</c> is written as a string because that is what SQS accepts in this
    /// document, whatever its name suggests.
    /// </remarks>
    private static string RedrivePolicy(string deadLetterQueueArn) =>
        $$"""
          {"deadLetterTargetArn":"{{deadLetterQueueArn}}","maxReceiveCount":"{{MaxReceiveCount}}"}
          """;

    /// <summary>
    /// The queue names, built the way <c>MessagingConstruct</c> builds them.
    /// </summary>
    /// <remarks>
    /// The shape is copied, not the environment: production suffixes the environment name, and this
    /// suffixes a value unique to one test. What is worth keeping is that the dead-letter queue is the
    /// source queue's name plus <c>-dlq</c>, because a reader triaging a real queue reads the same
    /// relationship.
    /// </remarks>
    private static class MessagingConstructNaming
    {
        internal static (string SourceQueueName, string DeadLetterQueueName) For(string suffix)
        {
            var sourceQueueName = $"reliable-orders-{suffix}";

            return (sourceQueueName, $"{sourceQueueName}-dlq");
        }
    }
}
