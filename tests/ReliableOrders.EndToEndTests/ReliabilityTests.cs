using ReliableOrders.Core.Observability;
using ReliableOrders.Core.Persistence;

namespace ReliableOrders.EndToEndTests;

/// <summary>
/// What the stack does with a message it can never process, and with two publishers that disagree.
/// </summary>
/// <remarks>
/// <para>
/// Both cases are slow by construction, and neither can be made fast without testing something else.
/// A poison message reaches the dead-letter queue after the redrive policy gives up on it, which is
/// the receive count multiplied by the visibility timeout — around a quarter of an hour against the
/// deployed configuration. Shortening either would demonstrate a stack nobody deploys.
/// </para>
/// <para>
/// They are a class of their own so they run beside the faster cases rather than after them. The
/// deadline is read from the deployed queue, so a configuration change moves it without an edit here.
/// </para>
/// </remarks>
[Trait(TestCategory.Name, TestCategory.EndToEnd)]
public sealed class ReliabilityTests(Deployment deployment, ITestOutputHelper output)
    : IClassFixture<Deployment>
{
    /// <summary>
    /// A message nothing can parse reaches the dead-letter queue, and only after the receives run out.
    /// </summary>
    [RequiresDeployment]
    public async Task A_poison_message_is_dead_lettered_when_its_receives_run_out()
    {
        var marker = $"poison-{Guid.NewGuid():N}";
        var (visibility, receives) = await deployment.Redrive();

        var sent = System.Diagnostics.Stopwatch.StartNew();

        _ = await deployment.SendRaw($"{EndToEndEvents.Poison(Run)}\"marker\":\"{marker}\"");

        var dead = await deployment.DeadLettered(marker, await deployment.DeadLetterDeadline());

        Assert.NotNull(dead);

        // How long it took is the assertion, because it is the part SQS cannot fake. A failed receive
        // hides the message for the visibility timeout, so a message that survived the receives the
        // policy allows cannot have reached this queue before the timeouts between them had elapsed.
        // A stack deployed with a receive count of one would arrive here in seconds.
        var floor = visibility * (receives - 1);

        Assert.True(
            sent.Elapsed >= floor,
            $"The message was dead-lettered after {sent.Elapsed}, which is sooner than the "
            + $"{receives} receives the policy allows could have been spent ({floor}).");

        // Written to the run rather than asserted on. The count is what the source queue reached, and
        // whether a redrive carries it across is the queue's business rather than this stack's — so
        // whoever reads a failure gets it, and the assertion above stands on its own.
        output.WriteLine(
            "Dead-lettered after {0}, reporting {1} receives against a policy of {2}.",
            sent.Elapsed,
            dead.Attributes?.GetValueOrDefault("ApproximateReceiveCount") ?? "an unreported number of",
            receives);
    }

    /// <summary>
    /// A second body under one event identifier is refused, and reported once however often it is
    /// redelivered.
    /// </summary>
    /// <remarks>
    /// The metric is the subject as much as the refusal is. A permanent failure is retried until the
    /// receives run out, and a metric published on each of them would turn one conflict into five
    /// data points — an alarm on "greater than zero" would then be five times as loud as the thing it
    /// is reporting. The publisher gates it on the first receive, and this is what says so against a
    /// real redelivery.
    /// </remarks>
    [RequiresDeployment]
    public async Task An_idempotency_conflict_is_refused_and_counted_once()
    {
        var since = DateTimeOffset.UtcNow.AddMinutes(-1);
        var published = EndToEndEvents.New(Run);

        _ = await deployment.Send(published);

        var order = await deployment.Order(published.Data.OrderId);

        Assert.NotNull(order);

        _ = await deployment.Send(EndToEndEvents.Conflicting(published));

        // The stored order is the first publisher's. A stack that overwrote it would have accepted
        // the second body under an identifier that already meant something else.
        await Task.Delay(DeploymentQueries.WriteVisible);

        var unchanged = await deployment.Order(published.Data.OrderId);

        Assert.NotNull(unchanged);
        Assert.Equal(
            order[OrderTableSchema.AmountMinor].N,
            unchanged[OrderTableSchema.AmountMinor].N);

        // Waited out past the redrive policy, so the retries have happened by the time the metric is
        // read. Reading it earlier would pass whether or not the gate works.
        await Task.Delay(await deployment.DeadLetterDeadline());

        // Settled rather than read once. CloudWatch surfaces per-minute points as it ingests them, so
        // a publisher that emitted one conflict per receive would show a sum of one on the first read
        // and five a minute later — and the first read is the one that would have passed.
        var conflicts = await deployment.MetricSum(
            MetricNames.IdempotencyConflicts,
            since,
            settle: TimeSpan.FromMinutes(2));

        Assert.Equal(1, conflicts);
    }

    /// <summary>What distinguishes this run's orders from another's.</summary>
    private static string Run { get; } = Deployment.EnvironmentName;
}
