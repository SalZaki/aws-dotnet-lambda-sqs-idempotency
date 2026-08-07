namespace ReliableOrders.Core.Idempotency;

/// <summary>
/// How long an idempotency record is kept after the event it claims occurred.
/// </summary>
/// <remarks>
/// <para>
/// Cleanup, not a correctness boundary. DynamoDB removes expired items asynchronously and on its own
/// schedule, so nothing may assume a record disappears at the instant it expires. After expiry a
/// replayed event falls through to the order-level check and is classified on its business hash,
/// which is only correct because the order item carries a hash of its own.
/// </para>
/// <para>
/// Configuration, supplied at the composition root from <c>IDEMPOTENCY_RETENTION_DAYS</c>. The
/// properties are get-only and there are no <c>init</c> accessors, so a <c>with</c> expression cannot
/// produce a retention that skipped the constructor's checks.
/// </para>
/// </remarks>
public sealed record IdempotencyRetention
{
    /// <summary>
    /// The longest retention that may be configured.
    /// </summary>
    /// <remarks>
    /// A year is far beyond any useful setting, and the cap exists to catch a mistyped environment
    /// variable at the cold start that reads it rather than as an expiry stamped tens of thousands of
    /// years out, which no operator would notice and no TTL sweep would ever reach.
    /// </remarks>
    public static readonly TimeSpan MaxConfigurableDuration = TimeSpan.FromDays(365);

    /// <summary>
    /// Constructs a retention, rejecting a duration that would defeat the record or overflow.
    /// </summary>
    /// <param name="duration">How long a record is kept after its event's <c>occurredAtUtc</c>.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The duration is not positive, or exceeds <see cref="MaxConfigurableDuration"/>.
    /// </exception>
    public IdempotencyRetention(TimeSpan duration)
    {
        // Zero is rejected as well as negative. A record written already expired protects nothing, and
        // the deployment would look healthy while duplicate detection silently did not run.
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero, nameof(duration));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(duration, MaxConfigurableDuration, nameof(duration));

        Duration = duration;
    }

    /// <summary>
    /// How long a record is kept after its event's <c>occurredAtUtc</c>.
    /// </summary>
    public TimeSpan Duration { get; }

    /// <summary>
    /// The retention recommended in docs/correctness-model.md.
    /// </summary>
    /// <remarks>
    /// Thirty days, comfortably beyond the four-day source queue retention and the fourteen-day
    /// dead-letter queue retention, so a message redriven from the dead-letter queue at the end of its
    /// life still meets the record that claims it.
    /// </remarks>
    public static IdempotencyRetention Default { get; } = new(TimeSpan.FromDays(30));
}
