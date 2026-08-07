using ReliableOrders.Core.Contracts;

namespace ReliableOrders.Core.Idempotency;

/// <summary>
/// Computes the two idempotency hashes for an event.
/// </summary>
/// <remarks>
/// Both hashes must be reproducible on any machine, in any process, under any future runtime. Every
/// stored idempotency record and every stored order carries a hash computed by an earlier build, and
/// a change to how these values are produced reclassifies all of them: replays that used to match
/// would begin to differ, and a benign redelivery would be reported as a conflict. Treat a change to
/// the computed value as a schema migration.
/// </remarks>
public interface IPayloadHasher
{
    /// <summary>
    /// Computes both hashes for a validated event.
    /// </summary>
    /// <param name="message">
    /// An event that has already passed validation. Hashing precedes persistence and follows
    /// validation, so the contract rules — a zero UTC offset above all — hold by the time this runs.
    /// </param>
    /// <returns>The envelope and business hashes, lowercase hexadecimal.</returns>
    PayloadHashes ComputeHashes(OrderCreatedV1 message);
}
