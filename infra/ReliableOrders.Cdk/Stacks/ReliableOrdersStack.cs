using Amazon.CDK;
using Constructs;
using ReliableOrders.Cdk.Configuration;
using ReliableOrders.Cdk.Constructs;

namespace ReliableOrders.Cdk.Stacks;

/// <summary>
/// One stack per environment, divided into constructs that each own a group of resources.
/// </summary>
/// <remarks>
/// Messaging is all that is here. Persistence, the function and its event source mapping, and the
/// dashboard and alarms each arrive as their own construct, so this stack composes rather than
/// declares.
/// </remarks>
public sealed class ReliableOrdersStack : Stack
{
    /// <summary>Identifies the project on every resource that can carry a tag.</summary>
    public const string ProjectTagValue = "ReliableOrdersWorker";

    /// <summary>Records that these resources are deployed rather than made by hand.</summary>
    public const string ManagedByTagValue = "CDK";

    /// <summary>
    /// Builds the stack for one environment.
    /// </summary>
    /// <param name="scope">The CDK application.</param>
    /// <param name="id">The stack name.</param>
    /// <param name="config">The environment's sizing, retention and runtime values.</param>
    /// <param name="props">Account, Region and stack-level settings.</param>
    public ReliableOrdersStack(Construct scope, string id, EnvironmentConfig config, IStackProps? props = null)
        : base(scope, id, props)
    {
        ArgumentNullException.ThrowIfNull(config);

        // Tagged on the stack rather than on each construct, so a resource added by a later story
        // carries the tags without anyone remembering to add them. ManagedBy tells an operator that
        // editing one of these queues in the console will be undone by the next deployment.
        //
        // Amazon.CDK.Tags is qualified because Stack has a Tags property of its own. That one is the
        // stack resource's tag manager and would tag the stack alone, where this is an aspect that
        // visits every child.
        var tags = Amazon.CDK.Tags.Of(this);
        tags.Add("Project", ProjectTagValue);
        tags.Add("Environment", config.EnvironmentName);
        tags.Add("ManagedBy", ManagedByTagValue);

        var messaging = new MessagingConstruct(this, "Messaging", config);

        // Both URLs are output because both are operational inputs. The publisher sends to one, and
        // the redrive runbook opens with the other.
        _ = new CfnOutput(this, "SourceQueueUrl", new CfnOutputProps
        {
            Value = messaging.SourceQueue.QueueUrl,
            Description = "The queue orders are published to.",
        });

        _ = new CfnOutput(this, "DeadLetterQueueUrl", new CfnOutputProps
        {
            Value = messaging.DeadLetterQueue.QueueUrl,
            Description = "The queue holding messages that exhausted their receives.",
        });
    }
}
