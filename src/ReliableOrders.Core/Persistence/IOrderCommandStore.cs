using ReliableOrders.Core.Contracts;
using ReliableOrders.Core.Idempotency;

namespace ReliableOrders.Core.Persistence;

/// <summary>
/// Creates an order and claims its event, atomically, or explains why it did not.
/// </summary>
/// <remarks>
/// <para>
/// One method on purpose. Separate <c>TryMarkAsync</c> and <c>SaveAsync</c> calls would make the
/// unsafe two-write sequence easy to write by accident — claim the event, stop before saving, and a
/// retry sees the claim and skips a message whose order was never stored. That is data loss with no
/// error anywhere, so the interface does not offer the shape.
/// </para>
/// <para>
/// The implementation is expected to fail by returning <see cref="OrderWriteResult"/>, not by
/// throwing. A caller that has to catch an SDK exception to tell a duplicate from a conflict is a
/// caller that knows which database is underneath.
/// </para>
/// <para>
/// Note the absence of a clock parameter. Every persisted value derives from the event, so accepting
/// one here would invite the determinism bug straight back in.
/// </para>
/// </remarks>
public interface IOrderCommandStore
{
    /// <summary>
    /// Attempts to create the order described by a validated event.
    /// </summary>
    /// <param name="message">An event that has already passed validation.</param>
    /// <param name="hashes">The hashes computed for that same event.</param>
    /// <param name="cancellationToken">
    /// Forwarded to the underlying call. Cancellation is not a transient fault and must not be
    /// reclassified as one — the invocation is ending, and reporting a downstream failure would
    /// misattribute it.
    /// </param>
    /// <returns>
    /// What happened, as a value. Never null, and never an exception for an outcome this hierarchy
    /// can express.
    /// </returns>
    Task<OrderWriteResult> TryCreateAsync(
        OrderCreatedV1 message,
        PayloadHashes hashes,
        CancellationToken cancellationToken);
}
