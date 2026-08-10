using Amazon.CDK;
using Amazon.CDK.AWS.SQS;
using Constructs;
using ReliableOrders.Cdk.Configuration;
using DeadLetterQueueProps = Amazon.CDK.AWS.SQS.DeadLetterQueue;

namespace ReliableOrders.Cdk.Constructs;

/// <summary>
/// The source queue orders arrive on and the dead-letter queue that holds what could not be processed.
/// </summary>
/// <remarks>
/// <para>
/// Both queues are named rather than left to CloudFormation, which is what lets the dead-letter queue
/// restrict redrive to this source queue. A redrive allow policy referencing the source queue resource
/// makes each queue depend on the other, and CloudFormation rejects that as a circular dependency.
/// With the name fixed, the ARN is composed from the stack's own partition, Region and account, which
/// reaches the same queue without referencing it.
/// </para>
/// <para>
/// The visibility timeout is read off <see cref="EnvironmentConfig.VisibilityTimeoutSeconds"/> rather
/// than taken as a parameter. A parameter would leave the CDK assertion comparing the number the test
/// supplied against the same number in the template, which passes whatever the formula does. Every
/// other value here is a plain setting and could be either way.
/// </para>
/// </remarks>
public sealed class MessagingConstruct : Construct
{
    /// <summary>
    /// Creates the source queue and its dead-letter queue.
    /// </summary>
    /// <param name="scope">The stack or construct these queues belong to.</param>
    /// <param name="id">The construct identifier, which prefixes both logical IDs.</param>
    /// <param name="config">Retention, redrive, and the values the visibility timeout is computed from.</param>
    public MessagingConstruct(Construct scope, string id, EnvironmentConfig config)
        : base(scope, id)
    {
        ArgumentNullException.ThrowIfNull(config);

        var sourceQueueName = $"reliable-orders-{config.EnvironmentName}";

        DeadLetterQueue = new Queue(this, "OrdersDeadLetterQueue", new QueueProps
        {
            QueueName = $"{sourceQueueName}-dlq",
            Encryption = QueueEncryption.SQS_MANAGED,
            EnforceSSL = true,
            RetentionPeriod = Duration.Days(config.DlqRetentionDays),

            // Retained wherever the tables are. Everything in this queue exhausted its retries and
            // has been diagnosed by nobody, so a destroy, or a change CloudFormation implements as a
            // replacement, discards the evidence the queue exists to hold. A retained queue keeps its
            // name and blocks the next deployment of that environment. That is the intended cost.
            RemovalPolicy = config.RetainData ? RemovalPolicy.RETAIN : RemovalPolicy.DESTROY,

            // Redrive from anywhere else is refused. Without this, any queue in the account may
            // nominate this one as its dead-letter queue, and messages from a queue nobody is
            // triaging arrive alongside the orders an operator is working through.
            RedriveAllowPolicy = new RedriveAllowPolicy
            {
                RedrivePermission = RedrivePermission.BY_QUEUE,
                SourceQueues =
                [
                    Queue.FromQueueArn(
                        this,
                        "OrdersQueueRedriveSource",
                        Stack.Of(this).FormatArn(new ArnComponents
                        {
                            Service = "sqs",
                            Resource = sourceQueueName,
                        })),
                ],
            },
        });

        SourceQueue = new Queue(this, "OrdersQueue", new QueueProps
        {
            QueueName = sourceQueueName,
            Encryption = QueueEncryption.SQS_MANAGED,
            EnforceSSL = true,
            RetentionPeriod = Duration.Days(config.SourceRetentionDays),
            VisibilityTimeout = Duration.Seconds(config.VisibilityTimeoutSeconds),

            // Zero, stated rather than defaulted. A delivery delay on this queue would add latency to
            // every order for the benefit of no consumer.
            DeliveryDelay = Duration.Seconds(0),

            // Long polling, which the event source mapping ignores because it manages its own. This
            // setting reaches the publisher CLI and any manual ReceiveMessage during triage, where
            // short polling returns empty responses on a queue that is not empty.
            ReceiveMessageWaitTime = Duration.Seconds(20),

            DeadLetterQueue = new DeadLetterQueueProps
            {
                Queue = DeadLetterQueue,
                MaxReceiveCount = config.MaxReceiveCount,
            },
        });
    }

    /// <summary>The queue orders arrive on.</summary>
    public IQueue SourceQueue { get; }

    /// <summary>The queue holding messages that exhausted their receives.</summary>
    public IQueue DeadLetterQueue { get; }
}
