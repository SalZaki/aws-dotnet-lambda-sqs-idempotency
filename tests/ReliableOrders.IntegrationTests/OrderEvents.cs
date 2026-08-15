using System.Text.Json;
using ReliableOrders.Core.Contracts;

namespace ReliableOrders.IntegrationTests;

/// <summary>
/// Events for tests that share one container.
/// </summary>
/// <remarks>
/// Shared rather than copied per test class. Two definitions of "a valid event" drift, and the moment
/// they disagree the suites stop testing the same contract while both stay green.
/// </remarks>
internal static class OrderEvents
{
    /// <summary>
    /// A valid event with identifiers nothing else will use.
    /// </summary>
    /// <remarks>
    /// Fresh identifiers per call, so the shared container needs no cleanup between tests and one test
    /// cannot pass because of a row another wrote. The timestamp is fixed rather than taken from the
    /// clock, so a stored value can be asserted exactly.
    /// </remarks>
    internal static OrderCreatedV1 New()
    {
        var id = Guid.NewGuid();

        return new OrderCreatedV1(
            SchemaVersion: OrderContract.SupportedSchemaVersion,
            EventId: id,
            EventType: OrderContract.ExpectedEventType,
            OccurredAtUtc: OccurredAt,
            Source: "integration.tests",
            CorrelationId: Guid.NewGuid(),
            CausationId: null,
            Data: new OrderData($"ORD-{id:N}", "CUS-90001", "GBP", 1299, "Mechanical keyboard"));
    }

    /// <summary>
    /// The instant the events above claim to have happened.
    /// </summary>
    /// <remarks>
    /// Exposed because the validator refuses an event more than five days old, so any test that
    /// publishes one and processes it has to hold a clock somewhere near it. Reading the value from
    /// here rather than restating it keeps the two from drifting apart, which would fail as a
    /// validation error naming a rule the test was not written about.
    /// </remarks>
    internal static DateTimeOffset OccurredAt { get; } = new(2026, 8, 1, 10, 30, 0, TimeSpan.Zero);

    /// <summary>
    /// A body a publisher would send, as the parser expects to read it.
    /// </summary>
    /// <remarks>
    /// Serialised through the contract's own context rather than through default options, so the
    /// property names are the ones the parser binds. Hand-written JSON would be a second definition of
    /// the wire format, and the tests would keep passing after the first one changed.
    /// </remarks>
    internal static string Serialize(OrderCreatedV1 orderEvent) =>
        JsonSerializer.Serialize(orderEvent, OrderContractSerializerContext.Default.OrderCreatedV1);

    /// <summary>
    /// A body no retry can fix.
    /// </summary>
    /// <remarks>
    /// Truncated JSON rather than a valid document with a bad field. Both are permanent failures, and
    /// this one fails at the first step, so it stays a poison message even if the contract changes
    /// underneath the test.
    /// </remarks>
    internal const string PoisonBody = "{ this is not json";
}
