using Amazon.CDK.Assertions;
using ReliableOrders.Cdk.Configuration;
using ReliableOrders.Cdk.Stacks;

namespace ReliableOrders.CdkTests.Messaging;

/// <summary>
/// What the messaging construct asks CloudFormation to create.
/// </summary>
/// <remarks>
/// Every case here runs against <see cref="Config"/> rather than the development defaults, and none
/// of its numbers are the documented ones. A construct that ignored its configuration and emitted the
/// defaults would pass a suite written against the defaults while deploying something nobody chose.
/// </remarks>
public sealed class MessagingConstructTests
{
    private const string EnvironmentName = "assert";
    private const string SourceQueueName = $"reliable-orders-{EnvironmentName}";
    private const string DeadLetterQueueName = $"{SourceQueueName}-dlq";

    private const int LambdaTimeoutSeconds = 45;
    private const int BatchWindowSeconds = 2;
    private const int VisibilityMarginSeconds = 30;
    private const int MaxReceiveCount = 7;
    private const int SourceRetentionDays = 5;
    private const int DlqRetentionDays = 12;

    private const int SecondsPerDay = 86_400;

    /// <summary>
    /// Scaled to this class's 302 second visibility timeout rather than reused from the development
    /// defaults, which pair 300 seconds with a 210 second timeout and would be refused here.
    /// </summary>
    private static readonly AlarmThresholds AlarmThresholds = new(
        oldestMessageAgeSeconds: 600,
        throttleEvaluationMinutes: 3,
        transientFailuresPerFiveMinutes: 10,
        noProgressMinutes: 15,
        deadlineDeferralsPerFiveMinutes: 1);

    /// <summary>
    /// The formula from docs/infrastructure.md, recomputed here from the values the construct was
    /// given.
    /// </summary>
    /// <remarks>
    /// Written out rather than read off <see cref="EnvironmentConfig.VisibilityTimeoutSeconds"/>,
    /// because comparing the derived property against itself would hold however the formula was
    /// changed. The construct not accepting a timeout is what makes this assertion mean something.
    /// </remarks>
    [Fact]
    public void The_visibility_timeout_is_computed_from_the_function_timeout_window_and_margin()
    {
        var timeout = Template().Queue(SourceQueueName).Number("VisibilityTimeout");

        Assert.Equal((6 * LambdaTimeoutSeconds) + BatchWindowSeconds + VisibilityMarginSeconds, timeout);
    }

    /// <summary>
    /// Retention on the dead-letter queue exceeds the source queue's, so a message has time left to be
    /// diagnosed after it stops being retried.
    /// </summary>
    [Fact]
    public void The_dead_letter_queue_keeps_messages_longer_than_the_source_queue()
    {
        var template = Template();

        var source = template.Queue(SourceQueueName).Number("MessageRetentionPeriod");
        var deadLetter = template.Queue(DeadLetterQueueName).Number("MessageRetentionPeriod");

        Assert.Equal(SourceRetentionDays * SecondsPerDay, source);
        Assert.Equal(DlqRetentionDays * SecondsPerDay, deadLetter);
        Assert.True(deadLetter > source, $"Dead-letter retention {deadLetter}s does not exceed source {source}s.");
    }

    /// <summary>
    /// The source queue dead-letters to this stack's dead-letter queue, after the configured receives.
    /// </summary>
    /// <remarks>
    /// The target is compared against the logical ID discovered from the template rather than a
    /// literal. Both halves of the policy are asserted, because a wrong target and a wrong count
    /// abandon messages the same way.
    /// </remarks>
    [Fact]
    public void The_source_queue_dead_letters_to_the_dead_letter_queue_after_the_configured_receives()
    {
        var template = Template();
        var redrivePolicy = template.Queue(SourceQueueName).Json("RedrivePolicy");

        Assert.Contains(template.Queue(DeadLetterQueueName).LogicalId, redrivePolicy, StringComparison.Ordinal);
        Assert.Contains($"\"maxReceiveCount\":{MaxReceiveCount}", redrivePolicy, StringComparison.Ordinal);
    }

    /// <summary>
    /// Redrive into the dead-letter queue is restricted to the source queue.
    /// </summary>
    /// <remarks>
    /// Left open, any queue in the account may nominate this one, and messages nobody is triaging
    /// arrive alongside the orders an operator is working through. The dead-letter queue's own name is
    /// asserted absent, because a policy naming itself would restrict nothing.
    /// </remarks>
    [Fact]
    public void The_dead_letter_queue_accepts_redrive_only_from_the_source_queue()
    {
        var allowPolicy = Template().Queue(DeadLetterQueueName).Json("RedriveAllowPolicy");

        Assert.Contains("byQueue", allowPolicy, StringComparison.Ordinal);
        Assert.Contains(SourceQueueName, allowPolicy, StringComparison.Ordinal);
        Assert.DoesNotContain(DeadLetterQueueName, allowPolicy, StringComparison.Ordinal);
    }

    /// <summary>
    /// Both queues are encrypted at rest.
    /// </summary>
    [Theory]
    [InlineData(SourceQueueName)]
    [InlineData(DeadLetterQueueName)]
    public void Every_queue_is_encrypted(string queueName)
    {
        Assert.True(Template().Queue(queueName).Flag("SqsManagedSseEnabled"));
    }

    /// <summary>
    /// Where the data is retained, the dead-letter queue survives the stack.
    /// </summary>
    /// <remarks>
    /// The source queue is asserted alongside it because it must not be retained. A retained queue
    /// keeps its physical name and blocks the next deployment, which is worth paying for undiagnosed
    /// failures and not for a queue that refills on its own.
    /// </remarks>
    [Theory]
    [InlineData(true, "Retain")]
    [InlineData(false, "Delete")]
    public void The_dead_letter_queue_survives_the_stack_where_data_is_retained(bool retainData, string expected)
    {
        var template = Template(retainData);

        Assert.Equal(expected, template.Queue(DeadLetterQueueName).DeletionPolicy);
        Assert.Equal("Delete", template.Queue(SourceQueueName).DeletionPolicy);
    }

    /// <summary>
    /// Both queues refuse calls that are not over TLS.
    /// </summary>
    /// <remarks>
    /// The statement is a resource of its own, so nothing about the queue looks wrong when it is
    /// missing. It is also the only control here that fails open, where a wrong retention or timeout
    /// at least shows a wrong number.
    /// </remarks>
    [Theory]
    [InlineData(SourceQueueName)]
    [InlineData(DeadLetterQueueName)]
    public void Every_queue_refuses_calls_that_are_not_over_tls(string queueName)
    {
        var template = Template();
        var policy = template.PolicyFor(template.Queue(queueName));

        Assert.Contains("\"aws:SecureTransport\":\"false\"", policy, StringComparison.Ordinal);
        Assert.Contains("\"Effect\":\"Deny\"", policy, StringComparison.Ordinal);
    }

    /// <summary>
    /// The source queue adds no delivery delay.
    /// </summary>
    /// <remarks>
    /// Zero is also the SQS default. It is stated in the construct and asserted here because a delay
    /// added later would cost every order latency and read as deliberate to whoever found it.
    /// </remarks>
    [Fact]
    public void The_source_queue_delays_nothing()
    {
        Assert.Equal(0, Template().Queue(SourceQueueName).Number("DelaySeconds"));
    }

    /// <summary>
    /// Both queues carry the project, environment and ownership tags.
    /// </summary>
    /// <remarks>
    /// ManagedBy is the one that earns its place. It tells an operator that a change made in the
    /// console will be reverted by the next deployment.
    /// </remarks>
    [Theory]
    [InlineData(SourceQueueName)]
    [InlineData(DeadLetterQueueName)]
    public void Every_queue_carries_the_project_environment_and_ownership_tags(string queueName)
    {
        var tags = Template().Queue(queueName).Tags();

        Assert.Equal(ReliableOrdersStack.ProjectTagValue, tags["Project"]);
        Assert.Equal(EnvironmentName, tags["Environment"]);
        Assert.Equal(ReliableOrdersStack.ManagedByTagValue, tags["ManagedBy"]);
    }

    /// <summary>
    /// The source queue polls long, which reaches the publisher CLI and manual triage.
    /// </summary>
    /// <remarks>
    /// The event source mapping manages its own polling and ignores this setting. It is here for the
    /// receivers that do not. Short polling samples a subset of hosts and returns nothing on a queue
    /// that is not empty, which during an incident reads as a queue that has drained.
    /// </remarks>
    [Fact]
    public void The_source_queue_polls_long()
    {
        Assert.Equal(20, Template().Queue(SourceQueueName).Number("ReceiveMessageWaitTimeSeconds"));
    }

    /// <summary>
    /// Both queue URLs are published, because both are operational inputs.
    /// </summary>
    [Theory]
    [InlineData("SourceQueueUrl")]
    [InlineData("DeadLetterQueueUrl")]
    public void The_stack_publishes_the_queue_urls(string outputName)
    {
        Template().HasOutput(outputName, Match.AnyValue());
    }

    private static Template Template(bool retainData = false) => SynthesizedStack.From(Config(retainData));

    /// <summary>
    /// Deliberately not the development defaults. See the remarks on the class.
    /// </summary>
    private static EnvironmentConfig Config(bool retainData = false) => new(
        environmentName: EnvironmentName,
        lambdaRuntimeIdentifier: "dotnet10",
        lambdaMemoryMb: 512,
        lambdaTimeoutSeconds: LambdaTimeoutSeconds,
        reservedConcurrency: 10,
        batchSize: 10,
        batchWindowSeconds: BatchWindowSeconds,
        maxConcurrency: 10,
        visibilityMarginSeconds: VisibilityMarginSeconds,
        maxReceiveCount: MaxReceiveCount,
        sourceRetentionDays: SourceRetentionDays,
        dlqRetentionDays: DlqRetentionDays,
        idempotencyRetentionDays: 30,
        retainData: retainData,
        enablePointInTimeRecovery: false,
        alarmThresholds: AlarmThresholds);
}
