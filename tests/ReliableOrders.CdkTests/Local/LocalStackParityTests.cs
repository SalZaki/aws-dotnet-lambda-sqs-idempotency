using ReliableOrders.Cdk.Configuration;
using ReliableOrders.Cdk.Constructs;
using ReliableOrders.Local;

namespace ReliableOrders.CdkTests.Local;

/// <summary>
/// The local development stack against the stack that is deployed.
/// </summary>
/// <remarks>
/// <para>
/// The local stack copies three things it cannot reference: the queue names, the redrive setting the
/// poison-message flow is counted against, and the handler the function is loaded by. Referencing the
/// CDK project from a program that runs in a container would pull Amazon.CDK.Lib and jsii in to
/// borrow two strings and an integer, and a Dockerfile can reference nothing at all.
/// </para>
/// <para>
/// So they are copies, and this is what stops them becoming stale ones. Each case reads the
/// synthesised template rather than the construct's own properties, for the reason
/// <c>SynthesizedStack</c> gives, and compares it against the constant the local stack actually uses.
/// </para>
/// <para>
/// Not everything is held in step, and the exceptions are deliberate. The visibility timeout is
/// meant to differ — the real queue computes 210 seconds and a local poison message would take five
/// of those to dead-letter, where the stand-in mapping resets visibility to zero — and the table
/// names differ because the deployed tables carry names CloudFormation generates, which is why the
/// function reads them from its environment at all.
/// </para>
/// </remarks>
public sealed class LocalStackParityTests
{
    /// <summary>
    /// The local stack names its queues the way the deployed stack names its own.
    /// </summary>
    /// <remarks>
    /// Both names, because the relationship between them is the part worth keeping: an operator
    /// triaging a real dead-letter queue reads the source queue's name plus <c>-dlq</c>, and a local
    /// stack that spelled it differently would teach the wrong shape.
    /// </remarks>
    [Fact]
    public void The_local_queues_are_named_the_way_the_stack_names_its_own()
    {
        var config = EnvironmentConfig.Development;
        var template = SynthesizedStack.From(config);

        var names = LocalQueues.NamesFor(config.EnvironmentName);

        // Queue throws naming what it did find, so a mismatch reports both names rather than a
        // boolean.
        _ = template.Queue(names.SourceQueueName);
        _ = template.Queue(names.DeadLetterQueueName);
    }

    /// <summary>
    /// A message survives the same number of receives locally as it does deployed.
    /// </summary>
    /// <remarks>
    /// The one redrive value the local stack has to match. The poison-message flow is a count of
    /// attempts, so a stack that dead-lettered after three would demonstrate something other than
    /// what a deployment does — and it would do it convincingly, which is worse than not
    /// demonstrating it at all.
    /// </remarks>
    [Fact]
    public void A_local_message_survives_the_receives_the_deployed_queue_allows()
    {
        var config = EnvironmentConfig.Development;
        var template = SynthesizedStack.From(config);

        var queue = template.Queue(LocalQueues.NamesFor(config.EnvironmentName).SourceQueueName);

        Assert.Contains(
            $"\"maxReceiveCount\":{LocalQueues.MaxReceiveCount}",
            queue.Json("RedrivePolicy"),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The local queue long-polls for as long as the deployed one does.
    /// </summary>
    /// <remarks>
    /// The event source mapping manages its own polling and ignores this, so it changes nothing about
    /// the flows. What reads it is a hand-run <c>ReceiveMessage</c> during triage, which is exactly
    /// what the README asks a developer to do — and short polling answers empty on a queue that is
    /// not.
    /// </remarks>
    [Fact]
    public void The_local_queue_long_polls_for_as_long_as_the_deployed_one()
    {
        var config = EnvironmentConfig.Development;
        var template = SynthesizedStack.From(config);

        var queue = template.Queue(LocalQueues.NamesFor(config.EnvironmentName).SourceQueueName);

        Assert.Equal(LocalQueues.ReceiveWaitSeconds, queue.Number("ReceiveMessageWaitTimeSeconds"));
    }

    /// <summary>
    /// The local mapping batches over the window the deployed mapping batches over.
    /// </summary>
    /// <remarks>
    /// What a batch is, is what a mixed-batch demonstration is about: one bad record must not cost
    /// the good ones their progress, and that claim is empty if the records never share a batch. A
    /// window that drifted from the deployed one would still batch, and would batch differently.
    /// </remarks>
    [Fact]
    public void The_local_mapping_batches_over_the_window_the_stack_deploys()
    {
        var template = SynthesizedStack.From(EnvironmentConfig.Development);

        var mapping = template.OnlyResource(EventSourceMappingResourceType);

        Assert.Equal(
            EventSourceMapping.BatchWindowSeconds,
            mapping.Number("MaximumBatchingWindowInSeconds"));
    }

    /// <summary>
    /// The local function is given the timeout the stack deploys.
    /// </summary>
    /// <remarks>
    /// The runtime interface emulator both reports this through <c>ILambdaContext.RemainingTime</c>
    /// and enforces it, so it is what the handler computes its deadline from and holds its margin
    /// back against. Left to the emulator's own default it is several minutes, and the deferral path
    /// becomes unreachable locally while the stack goes on claiming the invocation context is the
    /// real one — a difference nothing would report, because everything still passes.
    /// </remarks>
    [Fact]
    public void The_local_function_is_given_the_timeout_the_stack_deploys()
    {
        var compose = RepositoryFiles.Read(ComposeFile);

        Assert.Contains(
            $"{FunctionTimeoutVariable}: \"{EnvironmentConfig.Development.LambdaTimeoutSeconds}\"",
            compose,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The image runs the handler the stack deploys.
    /// </summary>
    /// <remarks>
    /// A Dockerfile can reference nothing, so the handler is written there as a literal — the third
    /// place this string appears, after the construct and the assembly it names. A mismatch fails on
    /// the first invocation of a demonstration, reported by the runtime as a type it cannot find,
    /// which is late and reads as a broken function rather than a stale string.
    /// </remarks>
    [Fact]
    public void The_local_image_runs_the_handler_the_stack_deploys()
    {
        var dockerfile = RepositoryFiles.Read(Path.Combine("local", "Dockerfile"));

        Assert.Contains(OrderProcessorConstruct.Handler, dockerfile, StringComparison.Ordinal);
    }

    /// <summary>
    /// The CloudFormation type the event source mapping is declared as.
    /// </summary>
    private const string EventSourceMappingResourceType = "AWS::Lambda::EventSourceMapping";

    /// <summary>
    /// The variable the runtime interface emulator reads its invocation timeout from.
    /// </summary>
    private const string FunctionTimeoutVariable = "AWS_LAMBDA_FUNCTION_TIMEOUT";

    /// <summary>
    /// Where the local development stack is defined.
    /// </summary>
    private const string ComposeFile = "compose.yaml";
}
