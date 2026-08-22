using ReliableOrders.Core.Observability;
using ReliableOrders.Core.Persistence;

namespace ReliableOrders.EndToEndTests;

/// <summary>
/// What the deployed stack does with a valid event, the same event twice, the same order again, and a
/// batch where one record is bad.
/// </summary>
/// <remarks>
/// These are the four claims the README makes, asserted against AWS rather than an emulator. Each
/// sends its own order, so the cases share a stack without sharing a subject and can run together.
/// </remarks>
[Trait(TestCategory.Name, TestCategory.EndToEnd)]
public sealed class OrderProcessingTests(Deployment deployment) : IClassFixture<Deployment>
{
    /// <summary>
    /// A valid event becomes one order carrying what was published.
    /// </summary>
    [RequiresDeployment]
    public async Task A_valid_event_is_stored_once()
    {
        var published = EndToEndEvents.New(Run);

        _ = await deployment.Send(published);

        var order = await deployment.Order(published.Data.OrderId);

        Assert.NotNull(order);
        Assert.Equal(published.Data.CustomerId, order[OrderTableSchema.CustomerId].S);
        Assert.Equal(published.Data.AmountMinor.ToString(Culture), order[OrderTableSchema.AmountMinor].N);
        Assert.Equal(published.EventId.ToString(), order[OrderTableSchema.EventId].S);

        // The event's own record, which is what a second delivery is refused against.
        var idempotency = await deployment.IdempotencyRecord(published.EventId);

        Assert.NotNull(idempotency);
        Assert.Equal(published.Data.OrderId, idempotency[IdempotencyTableSchema.OrderId].S);
    }

    /// <summary>
    /// The same event delivered again leaves one order, written by the first delivery.
    /// </summary>
    /// <remarks>
    /// Sent twice rather than waited for a redelivery, because at-least-once is a property of the
    /// queue and this is a claim about the stack: whatever causes the second delivery, the second
    /// write is refused. The assertion is the stored event identifier — a second write would carry
    /// the same one, so what proves the refusal is that the completion timestamp did not move.
    /// </remarks>
    [RequiresDeployment]
    public async Task The_same_event_again_writes_no_second_order()
    {
        var published = EndToEndEvents.New(Run);

        _ = await deployment.Send(published);
        _ = Assert.IsType<Dictionary<string, Amazon.DynamoDBv2.Model.AttributeValue>>(
            await deployment.Order(published.Data.OrderId));

        var first = await deployment.IdempotencyRecord(published.EventId);

        Assert.NotNull(first);

        _ = await deployment.Send(published);

        // Long enough for a second write to have happened if one were going to. There is no positive
        // signal for "nothing else happened", so the case waits out the window the first write took
        // and then reads again.
        await Task.Delay(DeploymentQueries.WriteVisible);

        var second = await deployment.IdempotencyRecord(published.EventId);

        Assert.NotNull(second);
        Assert.Equal(
            first[IdempotencyTableSchema.CompletedAtUtc].S,
            second[IdempotencyTableSchema.CompletedAtUtc].S);
    }

    /// <summary>
    /// The same order republished under a new event identifier is a duplicate, not a conflict.
    /// </summary>
    /// <remarks>
    /// The correction this project exists to make. One hash over the whole event would make every
    /// legitimate republish a conflict, routed to the dead-letter queue under a high-severity alarm —
    /// so the business hash decides this, and the envelope hash decides the case above.
    /// </remarks>
    [RequiresDeployment]
    public async Task A_republished_order_is_a_duplicate_rather_than_a_conflict()
    {
        var published = EndToEndEvents.New(Run);

        _ = await deployment.Send(published);

        var order = await deployment.Order(published.Data.OrderId);

        Assert.NotNull(order);

        var republished = EndToEndEvents.Republished(published);

        _ = await deployment.Send(republished);

        // The republished event gets a record of its own — it is a different event — while the order
        // stays as the first one wrote it.
        var record = await deployment.IdempotencyRecord(republished.EventId);

        Assert.NotNull(record);

        var unchanged = await deployment.Order(published.Data.OrderId);

        Assert.NotNull(unchanged);
        Assert.Equal(published.EventId.ToString(), unchanged[OrderTableSchema.EventId].S);
        Assert.Equal(order[OrderTableSchema.CreatedAtUtc].S, unchanged[OrderTableSchema.CreatedAtUtc].S);
    }

    /// <summary>
    /// One bad record in a batch does not cost the good ones a redelivery.
    /// </summary>
    /// <remarks>
    /// The partial batch response is what this asserts, and the log is where it is visible. Each valid
    /// record is processed exactly once: a batch reported as wholly failed would replay them, and the
    /// second processing would appear here as a second line for the same event.
    /// </remarks>
    [RequiresDeployment]
    public async Task A_bad_record_does_not_replay_the_batch()
    {
        var first = EndToEndEvents.New(Run);
        var second = EndToEndEvents.New(Run);

        await deployment.SendBatch(
        [
            EndToEndEvents.Serialize(first),
            EndToEndEvents.Poison(Run),
            EndToEndEvents.Serialize(second),
        ]);

        foreach (var published in new[] { first, second })
        {
            Assert.NotNull(await deployment.Order(published.Data.OrderId));
        }

        // Waited out rather than read once, for the reason the duplicate case gives: a replay would
        // arrive after the visibility timeout, so a read taken immediately would pass either way.
        await Task.Delay(DeploymentQueries.WriteVisible);

        foreach (var published in new[] { first, second })
        {
            var lines = await deployment.LogLines(
                $"{{ $.{LogFields.EventId} = \"{published.EventId}\" && $.{LogFields.Outcome} = \"*\" }}",
                atLeast: 1,
                DeploymentQueries.WriteVisible);

            Assert.Single(lines);
        }
    }

    /// <summary>What distinguishes this run's orders from another's.</summary>
    private static string Run { get; } = Deployment.EnvironmentName;

    private static readonly System.Globalization.CultureInfo Culture =
        System.Globalization.CultureInfo.InvariantCulture;
}
