namespace ReliableOrders.Core.Validation;

/// <summary>
/// How far from processing time an event's <c>occurredAtUtc</c> may sit.
/// </summary>
/// <remarks>
/// <para>
/// A validation rule, not a correctness mechanism. Idempotency does not depend on it; it exists to
/// stop a clock-skewed or replayed publisher writing orders stamped years out.
/// </para>
/// <para>
/// Both bounds are configuration, supplied at the composition root from
/// <c>MAX_EVENT_SKEW_FUTURE_HOURS</c> and <c>MAX_EVENT_SKEW_PAST_DAYS</c>.
/// </para>
/// <para>
/// The properties are get-only and there are no <c>init</c> accessors, so a <c>with</c> expression
/// cannot produce a window that skipped the constructor's checks.
/// </para>
/// </remarks>
public sealed record EventSkewWindow
{
    /// <summary>
    /// The largest bound either direction may be given.
    /// </summary>
    /// <remarks>
    /// A year is far beyond any legitimate setting, and the cap exists to catch a mistyped
    /// environment variable at the cold start that reads it. Without it, a bound of a few million
    /// days overflows when added to processing time, and the first sign of the typo is an unhandled
    /// exception on every message rather than a configuration failure with a named variable.
    /// </remarks>
    public static readonly TimeSpan MaxConfigurableBound = TimeSpan.FromDays(365);

    /// <summary>
    /// Constructs a window, rejecting bounds that would make every event invalid or overflow.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Either bound is negative or exceeds <see cref="MaxConfigurableBound"/>.
    /// </exception>
    public EventSkewWindow(TimeSpan maxFuture, TimeSpan maxPast)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFuture, TimeSpan.Zero, nameof(maxFuture));
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPast, TimeSpan.Zero, nameof(maxPast));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxFuture, MaxConfigurableBound, nameof(maxFuture));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxPast, MaxConfigurableBound, nameof(maxPast));

        MaxFuture = maxFuture;
        MaxPast = maxPast;
    }

    /// <summary>
    /// How far ahead of processing time an event may be stamped.
    /// </summary>
    public TimeSpan MaxFuture { get; }

    /// <summary>
    /// How far behind processing time an event may be stamped.
    /// </summary>
    public TimeSpan MaxPast { get; }

    /// <summary>
    /// The bounds recommended in docs/event-contract.md.
    /// </summary>
    /// <remarks>
    /// The past bound is the source queue's four-day retention plus one day, so a message that sat in
    /// the queue for its whole life still validates on the last delivery attempt. Tightening it below
    /// retention would dead-letter messages for being old when the queue is what made them old.
    /// </remarks>
    public static EventSkewWindow Default { get; } = new(TimeSpan.FromHours(24), TimeSpan.FromDays(5));
}
