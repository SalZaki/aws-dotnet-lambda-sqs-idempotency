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
            OccurredAtUtc: new DateTimeOffset(2026, 8, 1, 10, 30, 0, TimeSpan.Zero),
            Source: "integration.tests",
            CorrelationId: Guid.NewGuid(),
            CausationId: null,
            Data: new OrderData($"ORD-{id:N}", "CUS-90001", "GBP", 1299, "Mechanical keyboard"));
    }
}
