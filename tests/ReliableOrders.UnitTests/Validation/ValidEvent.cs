using ReliableOrders.Core.Contracts;

namespace ReliableOrders.UnitTests.Validation;

/// <summary>
/// A known-good event, and the instant it is valid at.
/// </summary>
/// <remarks>
/// Every negative test starts from this and breaks exactly one field, so a failure is attributable to
/// the field the test names rather than to the fixture.
/// </remarks>
internal static class ValidEvent
{
    /// <summary>
    /// Processing time for the tests. Fixed rather than taken from the clock, so a test cannot pass
    /// or fail according to when it runs.
    /// </summary>
    internal static readonly DateTimeOffset Now = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    internal static OrderCreatedV1 Create() => new(
        SchemaVersion: OrderContract.SupportedSchemaVersion,
        EventId: Guid.Parse("0d76e91c-44e6-4fba-901f-bfdb76645299"),
        EventType: OrderContract.ExpectedEventType,
        OccurredAtUtc: Now.AddMinutes(-5),
        Source: "sample.order-publisher",
        CorrelationId: Guid.Parse("f1e02471-f9da-437f-bc32-e4e65394658a"),
        CausationId: null,
        Data: new OrderData("ORD-100001", "CUS-90001", "GBP", 1299, "Mechanical keyboard"));

    internal static OrderData Data() => Create().Data;
}
