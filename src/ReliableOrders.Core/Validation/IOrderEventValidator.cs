using ReliableOrders.Core.Contracts;

namespace ReliableOrders.Core.Validation;

/// <summary>
/// Checks a parsed event against the contract rules in docs/event-contract.md.
/// </summary>
/// <remarks>
/// Separate from parsing. Parsing answers whether the body is an order event of a known version;
/// this answers whether its values are usable. Keeping them apart is what lets a malformed body and
/// a negative amount be classified, logged and alarmed differently.
/// </remarks>
public interface IOrderEventValidator
{
    /// <summary>
    /// Returns every rule the event broke, and never rewrites the event to make it valid.
    /// </summary>
    /// <remarks>
    /// No contents of <paramref name="orderEvent"/> cause a throw, however malformed. A null
    /// reference does, because that is a caller defect rather than a bad event.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="orderEvent"/> is null.</exception>
    ValidationResult Validate(OrderCreatedV1 orderEvent);
}
