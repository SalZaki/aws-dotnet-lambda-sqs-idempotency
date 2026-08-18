namespace ReliableOrders.Cdk.Configuration;

/// <summary>
/// The numbers behind the alarms that docs/observability.md leaves as "a threshold".
/// </summary>
/// <remarks>
/// <para>
/// Held apart from the rest of <see cref="EnvironmentConfig"/> because they are read and changed as a
/// set. An operator tuning alerting touches these and nothing else, and the alternative is five more
/// parameters on a constructor that already takes fifteen.
/// </para>
/// <para>
/// Each window is named into the property rather than carried as a second value, so a count cannot be
/// read without the period it applies over.
/// </para>
/// <para>
/// Two of these only make sense against values that live outside this record, so they are checked in
/// the <see cref="EnvironmentConfig"/> constructor instead of here.
/// </para>
/// </remarks>
public sealed record AlarmThresholds
{
    /// <summary>
    /// The period every metric behind these thresholds is aggregated over.
    /// </summary>
    /// <remarks>
    /// Declared here rather than in the construct that builds the alarms, because it is what the
    /// per-five-minutes property names mean and what the counted windows are divided into. One
    /// declaration, so the divisor a window is validated against is the divisor it is later converted
    /// with.
    /// </remarks>
    public const int AggregationPeriodMinutes = 5;

    /// <summary>
    /// Builds the set. A threshold of zero or less would alarm on an idle queue, so it is refused.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A value is zero or negative.</exception>
    public AlarmThresholds(
        int oldestMessageAgeSeconds,
        int throttleEvaluationMinutes,
        int transientFailuresPerFiveMinutes,
        int noProgressMinutes,
        int deadlineDeferralsPerFiveMinutes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(oldestMessageAgeSeconds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(throttleEvaluationMinutes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(transientFailuresPerFiveMinutes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(noProgressMinutes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(deadlineDeferralsPerFiveMinutes);

        // The no-progress window is deployed as a count of aggregation periods, so a window that is not
        // a whole number of them is silently rescaled to the one below it. Twelve minutes would deploy
        // as ten, and anything under five as an alarm of zero evaluation periods, which is not an alarm.
        if (noProgressMinutes % AggregationPeriodMinutes != 0)
        {
            throw new ArgumentException(
                $"No-progress window {noProgressMinutes} minutes is not a multiple of the "
                + $"{AggregationPeriodMinutes} minute aggregation period. The window is deployed as a "
                + "count of those periods, so a remainder is discarded rather than rounded.",
                nameof(noProgressMinutes));
        }

        OldestMessageAgeSeconds = oldestMessageAgeSeconds;
        ThrottleEvaluationMinutes = throttleEvaluationMinutes;
        TransientFailuresPerFiveMinutes = transientFailuresPerFiveMinutes;
        NoProgressMinutes = noProgressMinutes;
        DeadlineDeferralsPerFiveMinutes = deadlineDeferralsPerFiveMinutes;
    }

    /// <summary>
    /// The development thresholds, sized against the development configuration in
    /// docs/infrastructure.md rather than chosen in the abstract.
    /// </summary>
    public static AlarmThresholds Development { get; } = new(
        oldestMessageAgeSeconds: 300,
        throttleEvaluationMinutes: 3,
        transientFailuresPerFiveMinutes: 10,
        noProgressMinutes: 15,
        deadlineDeferralsPerFiveMinutes: 1);

    /// <summary>
    /// How old the oldest source-queue message may be before alarm 3 fires.
    /// </summary>
    /// <remarks>
    /// Must exceed the visibility timeout, which the <see cref="EnvironmentConfig"/> constructor
    /// enforces. A message that failed once and is waiting out its visibility timeout is behaving
    /// normally, so a threshold below that alarms on the retry path working as designed.
    /// </remarks>
    public int OldestMessageAgeSeconds { get; }

    /// <summary>
    /// How many consecutive minutes of throttling alarm 4 waits for.
    /// </summary>
    /// <remarks>
    /// The alarm is on throttles above zero, so the sustained period is the only thing left to tune. A
    /// single throttled invocation against reserved concurrency is the mechanism working; several
    /// minutes of them is saturation.
    /// </remarks>
    public int ThrottleEvaluationMinutes { get; }

    /// <summary>
    /// Transient record failures in five minutes before alarm 5 fires.
    /// </summary>
    /// <remarks>
    /// Must exceed the maximum receive count, which the <see cref="EnvironmentConfig"/> constructor
    /// enforces. Transient failures are not gated on first receipt the way the permanent-failure
    /// metrics are, so one message exhausting its retries emits one sample per attempt. A threshold at
    /// or below that count turns a single poison message into an alarm.
    /// </remarks>
    public int TransientFailuresPerFiveMinutes { get; }

    /// <summary>
    /// How long alarm 7 tolerates a non-empty queue with no successful processing.
    /// </summary>
    /// <remarks>
    /// Measured against the sum of <c>OrdersProcessed</c> and <c>DuplicateEvents</c>, never the first
    /// alone. A replay storm is processed correctly while new orders stay flat, and the window has to
    /// be long enough that a queue quiet between bursts is not read as a stall.
    /// </remarks>
    public int NoProgressMinutes { get; }

    /// <summary>
    /// Deadline deferrals in five minutes before alarm 8 fires.
    /// </summary>
    /// <remarks>
    /// A deferral means the invocation ran out of time to start work it had already received, so the
    /// batch size or the deadline margin needs revisiting. A tuning signal rather than a fault, so the
    /// threshold is one deferral and not a rate.
    /// </remarks>
    public int DeadlineDeferralsPerFiveMinutes { get; }
}
