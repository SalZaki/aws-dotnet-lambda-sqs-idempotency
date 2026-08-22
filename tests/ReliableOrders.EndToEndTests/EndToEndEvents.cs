using System.Text.Json;
using ReliableOrders.Core.Contracts;

namespace ReliableOrders.EndToEndTests;

/// <summary>
/// Events built for one run against a real account.
/// </summary>
/// <remarks>
/// <para>
/// Built rather than read from <c>samples/</c>, and the timestamp is why. Those fixtures carry fixed
/// instants because several suites assert on their hashes, and the deployed validator refuses an
/// event more than a day ahead or five days behind. A run that sent them would demonstrate the skew
/// rule rather than the idempotency model — see revision log entry 139, which widened the window for
/// the local stack and could not widen it here, because what this suite exists to test is the
/// configuration that ships.
/// </para>
/// <para>
/// Identifiers carry the run, so a stack that outlived its workflow can be read afterwards and the
/// rows traced back to what wrote them.
/// </para>
/// </remarks>
internal static class EndToEndEvents
{
    /// <summary>
    /// A valid event for an order nothing else in this run will touch.
    /// </summary>
    /// <param name="run">What distinguishes this run, which reaches the order identifier.</param>
    /// <remarks>
    /// The whole GUID survives, and the run is what gets shortened. Truncating the identifier as a
    /// whole left however many characters the run had not already spent — four, against a workflow
    /// run identifier of today's length, which is eight orders a night drawn from sixty-five thousand
    /// and a collision that reads as a spurious duplicate. A longer run identifier would have left
    /// none at all, and every order in the run would have shared one.
    /// </remarks>
    internal static OrderCreatedV1 New(string run) => new(
        SchemaVersion: OrderContract.SupportedSchemaVersion,
        EventId: Guid.NewGuid(),
        EventType: OrderContract.ExpectedEventType,
        OccurredAtUtc: Now(),
        Source: "endtoend.tests",
        CorrelationId: Guid.NewGuid(),
        CausationId: null,
        Data: new OrderData(
            OrderId: $"ORD-{Shortened(run)}-{Guid.NewGuid():N}",
            CustomerId: "CUS-90001",
            Currency: "GBP",
            AmountMinor: 1299,
            ItemDescription: "Mechanical keyboard"));

    /// <summary>
    /// The same order, published again under a new event.
    /// </summary>
    /// <remarks>
    /// The business fields are identical and the envelope is not, which is what makes this a
    /// duplicate rather than a conflict. It carries the first event as its causation, the way a
    /// republishing publisher would.
    /// </remarks>
    internal static OrderCreatedV1 Republished(OrderCreatedV1 original) => original with
    {
        EventId = Guid.NewGuid(),
        OccurredAtUtc = Now(),
        CorrelationId = Guid.NewGuid(),
        CausationId = original.EventId,
    };

    /// <summary>
    /// The same event identifier carrying different business data, which is a conflict.
    /// </summary>
    /// <remarks>
    /// One event identifier is one event. A second body under it means two publishers disagreed about
    /// what happened, and the stack is required to refuse the second rather than overwrite the first.
    /// </remarks>
    internal static OrderCreatedV1 Conflicting(OrderCreatedV1 original) => original with
    {
        Data = original.Data with { AmountMinor = original.Data.AmountMinor + 100 },
    };

    /// <summary>
    /// A body no parser will read, which is a permanent failure on arrival.
    /// </summary>
    /// <remarks>
    /// Malformed rather than merely invalid, so that nothing about it depends on a validation rule
    /// that might be relaxed. What it demonstrates is the redrive policy: it fails on every receive
    /// and the queue gives up on it after the configured count.
    /// </remarks>
    internal static string Poison(string run) =>
        $"{{\"schemaVersion\":1,\"eventId\":\"not-a-guid\",\"run\":\"{run}\",";

    /// <summary>
    /// As much of the run as an order identifier can spare.
    /// </summary>
    /// <remarks>
    /// The identifier is four characters of prefix, the run, a separator and thirty-two of GUID,
    /// against a contract maximum of <see cref="OrderContract.MaxOrderIdLength"/>. The run is there
    /// to make a row traceable to the workflow that wrote it, which its first characters do; the GUID
    /// is what makes it unique, so the GUID is the part that is kept whole.
    /// </remarks>
    private static string Shortened(string run) =>
        run.Length <= MaximumRunInOrderId ? run : run[..MaximumRunInOrderId];

    /// <summary>What is left for the run once the prefix, separator and GUID have their share.</summary>
    private const int MaximumRunInOrderId = OrderContract.MaxOrderIdLength - 4 - 1 - 32;

    /// <summary>Serialised through the contract's own context, so the names are the ones bound.</summary>
    internal static string Serialize(OrderCreatedV1 orderEvent) =>
        JsonSerializer.Serialize(orderEvent, OrderContractSerializerContext.Default.OrderCreatedV1);

    /// <summary>
    /// Now, to the second.
    /// </summary>
    /// <remarks>
    /// Truncated because the stored value is compared in assertions and a round-trip through JSON and
    /// DynamoDB keeps whatever precision it was given, which is more than a reader wants to see.
    /// </remarks>
    private static DateTimeOffset Now() =>
        new(DateTimeOffset.UtcNow.UtcDateTime.AddTicks(-(DateTimeOffset.UtcNow.UtcDateTime.Ticks % TimeSpan.TicksPerSecond)), TimeSpan.Zero);
}
