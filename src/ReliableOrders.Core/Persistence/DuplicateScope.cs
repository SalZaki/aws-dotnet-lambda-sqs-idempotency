namespace ReliableOrders.Core.Persistence;

/// <summary>
/// Which safeguard recognised a write as one already performed.
/// </summary>
/// <remarks>
/// Both values are successes. The scope is carried because the two mean different things to an
/// operator reading a log — one is the same message arriving twice, the other is the same order
/// published twice — and because they are counted separately.
/// </remarks>
public enum DuplicateScope
{
    /// <summary>
    /// The same <c>eventId</c> was already claimed, with a matching envelope hash. An at-least-once
    /// redelivery of one message.
    /// </summary>
    Event,

    /// <summary>
    /// A different <c>eventId</c>, but the order already exists with a matching business hash. A
    /// republish of the same logical order, which is why the business hash exists.
    /// </summary>
    Order,
}
