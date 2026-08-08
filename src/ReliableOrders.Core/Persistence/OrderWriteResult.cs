namespace ReliableOrders.Core.Persistence;

/// <summary>
/// The outcome of attempting to create one order.
/// </summary>
/// <remarks>
/// <para>
/// Every outcome the store can reach is a value here, including the ones a naive implementation would
/// express by throwing. A caller therefore decides what to do with a message without catching an AWS
/// exception or knowing that DynamoDB was involved, which is what keeps the processing pipeline
/// transport-neutral.
/// </para>
/// <para>
/// The <c>private protected</c> constructor prevents another assembly adding a case. It does not make
/// a <c>switch</c> exhaustive: C# has no closed hierarchies, and a switch expression covering all five
/// cases with no discard arm still fails with CS8509, which is an error here.
/// </para>
/// <para>
/// <see cref="Match{TResult}"/> provides that guarantee instead. Each case is a parameter, so adding
/// one breaks every call site at compile time and no site can fall through to a default. Missing a
/// case here would acknowledge a message that was never stored, so it is used rather than a switch.
/// </para>
/// </remarks>
public abstract record OrderWriteResult
{
    private protected OrderWriteResult() { }

    /// <summary>
    /// Applies the handler for this result's case.
    /// </summary>
    public abstract TResult Match<TResult>(
        Func<Created, TResult> whenCreated,
        Func<Duplicate, TResult> whenDuplicate,
        Func<Conflict, TResult> whenConflict,
        Func<TransientFault, TResult> whenTransientFault,
        Func<PermanentFault, TResult> whenPermanentFault);

    /// <summary>
    /// The order and its idempotency record were written, atomically, by this attempt.
    /// </summary>
    public sealed record Created : OrderWriteResult
    {
        /// <inheritdoc/>
        public override TResult Match<TResult>(
            Func<Created, TResult> whenCreated,
            Func<Duplicate, TResult> whenDuplicate,
            Func<Conflict, TResult> whenConflict,
            Func<TransientFault, TResult> whenTransientFault,
            Func<PermanentFault, TResult> whenPermanentFault) => whenCreated(this);
    }

    /// <summary>
    /// The work was already done, and the stored data agrees with this message.
    /// </summary>
    /// <remarks>
    /// A success. The message is acknowledged and nothing is written. Distinguishing this from
    /// <see cref="Conflict"/> is the entire purpose of storing two hashes rather than one.
    /// </remarks>
    /// <param name="Scope">Which safeguard recognised it.</param>
    public sealed record Duplicate(DuplicateScope Scope) : OrderWriteResult
    {
        /// <inheritdoc/>
        public override TResult Match<TResult>(
            Func<Created, TResult> whenCreated,
            Func<Duplicate, TResult> whenDuplicate,
            Func<Conflict, TResult> whenConflict,
            Func<TransientFault, TResult> whenTransientFault,
            Func<PermanentFault, TResult> whenPermanentFault) => whenDuplicate(this);
    }

    /// <summary>
    /// Something is already stored under this identity, and it does not match this message.
    /// </summary>
    /// <remarks>
    /// Permanent, and alarms. Two payloads disagree about one identity, so no retry can settle it and
    /// a person has to look at the publisher.
    /// </remarks>
    /// <param name="Scope">Which safeguard refused it.</param>
    /// <param name="Reason">A value from <see cref="WriteFailureReason"/>, safe to log and to count.</param>
    public sealed record Conflict(ConflictScope Scope, string Reason) : OrderWriteResult
    {
        /// <inheritdoc/>
        public override TResult Match<TResult>(
            Func<Created, TResult> whenCreated,
            Func<Duplicate, TResult> whenDuplicate,
            Func<Conflict, TResult> whenConflict,
            Func<TransientFault, TResult> whenTransientFault,
            Func<PermanentFault, TResult> whenPermanentFault) => whenConflict(this);
    }

    /// <summary>
    /// The attempt failed for a reason that may not recur.
    /// </summary>
    /// <remarks>
    /// The message is returned as a batch item failure and redelivered. Nothing was written, because
    /// the transaction is atomic.
    /// </remarks>
    /// <param name="Reason">A value from <see cref="WriteFailureReason"/>.</param>
    public sealed record TransientFault(string Reason) : OrderWriteResult
    {
        /// <inheritdoc/>
        public override TResult Match<TResult>(
            Func<Created, TResult> whenCreated,
            Func<Duplicate, TResult> whenDuplicate,
            Func<Conflict, TResult> whenConflict,
            Func<TransientFault, TResult> whenTransientFault,
            Func<PermanentFault, TResult> whenPermanentFault) => whenTransientFault(this);
    }

    /// <summary>
    /// The request itself is wrong, and every retry will fail identically.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Separate from <see cref="Conflict"/> because the operational response is the opposite. A
    /// conflict means a publisher sent contradictory data and the code behaved correctly; this means
    /// the code built a request DynamoDB will not accept, and no publisher can fix it.
    /// </para>
    /// <para>
    /// Separate from <see cref="TransientFault"/> because retrying wastes the message's remaining
    /// receive attempts and then dead-letters it, having reported a downstream fault for what is a
    /// defect in this service.
    /// </para>
    /// </remarks>
    /// <param name="Reason">A value from <see cref="WriteFailureReason"/>.</param>
    public sealed record PermanentFault(string Reason) : OrderWriteResult
    {
        /// <inheritdoc/>
        public override TResult Match<TResult>(
            Func<Created, TResult> whenCreated,
            Func<Duplicate, TResult> whenDuplicate,
            Func<Conflict, TResult> whenConflict,
            Func<TransientFault, TResult> whenTransientFault,
            Func<PermanentFault, TResult> whenPermanentFault) => whenPermanentFault(this);
    }
}
