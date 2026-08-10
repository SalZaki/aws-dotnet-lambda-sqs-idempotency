using System.Globalization;

namespace ReliableOrders.Cdk.Configuration;

/// <summary>
/// Everything the stack is allowed to vary between environments.
/// </summary>
/// <remarks>
/// <para>
/// A construct reads what it needs from here rather than taking it as a parameter. See
/// <see cref="Constructs.MessagingConstruct"/> for what that buys and why it is not optional.
/// </para>
/// <para>
/// The invariants are checked on construction because CloudFormation accepts every combination this
/// type rejects. Concurrency above the function's reserved concurrency deploys and then throttles
/// under load. Dead-letter retention shorter than source retention deploys and then deletes the
/// evidence an operator came looking for.
/// </para>
/// <para>
/// Build a new instance rather than deriving one with <c>with</c>. A record copy assigns the backing
/// fields directly and re-runs none of these checks.
/// </para>
/// </remarks>
public sealed record EnvironmentConfig
{
    /// <summary>
    /// AWS guidance, not a tuning value. The visibility timeout must be at least six times the
    /// function timeout.
    /// </summary>
    private const int VisibilityTimeoutMultiplier = 6;

    /// <summary>
    /// Fewer receives than this dead-letters messages that a burst of throttling would have cleared.
    /// </summary>
    private const int MinimumReceiveCount = 5;

    /// <summary>
    /// Builds a configuration, checking each value and the relationships between them. The properties
    /// below document the parameters they take their names from.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Two values contradict each other. The message quotes both.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">A value is zero or negative.</exception>
    public EnvironmentConfig(
        string environmentName,
        string lambdaRuntimeIdentifier,
        int lambdaMemoryMb,
        int lambdaTimeoutSeconds,
        int reservedConcurrency,
        int batchSize,
        int batchWindowSeconds,
        int maxConcurrency,
        int visibilityMarginSeconds,
        int maxReceiveCount,
        int sourceRetentionDays,
        int dlqRetentionDays,
        int idempotencyRetentionDays,
        bool retainData,
        bool enablePointInTimeRecovery)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentName);
        ArgumentException.ThrowIfNullOrWhiteSpace(lambdaRuntimeIdentifier);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lambdaMemoryMb);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lambdaTimeoutSeconds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(reservedConcurrency);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        ArgumentOutOfRangeException.ThrowIfNegative(batchWindowSeconds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConcurrency);
        ArgumentOutOfRangeException.ThrowIfNegative(visibilityMarginSeconds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceRetentionDays);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(idempotencyRetentionDays);

        // The three cross-value rules from the AWS CDK Design section of docs/infrastructure.md. Each
        // message quotes both numbers, because either can be the wrong one and only the reader knows
        // which.
        if (maxConcurrency > reservedConcurrency)
        {
            throw new ArgumentException(
                $"Maximum concurrency {maxConcurrency} exceeds reserved concurrency {reservedConcurrency}. "
                + "The event source cannot request more concurrent executions than the function may use.",
                nameof(maxConcurrency));
        }

        if (dlqRetentionDays <= sourceRetentionDays)
        {
            throw new ArgumentException(
                $"Dead-letter retention {dlqRetentionDays} days does not exceed source retention "
                + $"{sourceRetentionDays} days. A message that spent the whole source retention failing "
                + "needs longer than that to be diagnosed.",
                nameof(dlqRetentionDays));
        }

        if (maxReceiveCount < MinimumReceiveCount)
        {
            throw new ArgumentException(
                $"Maximum receive count {maxReceiveCount} is below the minimum of {MinimumReceiveCount}. "
                + "A lower count dead-letters messages that a transient throttle would have cleared.",
                nameof(maxReceiveCount));
        }

        EnvironmentName = environmentName;
        LambdaRuntimeIdentifier = lambdaRuntimeIdentifier;
        LambdaMemoryMb = lambdaMemoryMb;
        LambdaTimeoutSeconds = lambdaTimeoutSeconds;
        ReservedConcurrency = reservedConcurrency;
        BatchSize = batchSize;
        BatchWindowSeconds = batchWindowSeconds;
        MaxConcurrency = maxConcurrency;
        VisibilityMarginSeconds = visibilityMarginSeconds;
        MaxReceiveCount = maxReceiveCount;
        SourceRetentionDays = sourceRetentionDays;
        DlqRetentionDays = dlqRetentionDays;
        IdempotencyRetentionDays = idempotencyRetentionDays;
        RetainData = retainData;
        EnablePointInTimeRecovery = enablePointInTimeRecovery;
    }

    /// <summary>
    /// The development defaults from docs/infrastructure.md. They evaluate to a 210 second visibility
    /// timeout.
    /// </summary>
    public static EnvironmentConfig Development { get; } = new(
        environmentName: "dev",
        lambdaRuntimeIdentifier: "dotnet10",
        lambdaMemoryMb: 512,
        lambdaTimeoutSeconds: 30,
        reservedConcurrency: 10,
        batchSize: 10,
        batchWindowSeconds: 1,
        maxConcurrency: 10,
        visibilityMarginSeconds: 29,
        maxReceiveCount: 5,
        sourceRetentionDays: 4,
        dlqRetentionDays: 14,
        idempotencyRetentionDays: 30,
        retainData: false,
        enablePointInTimeRecovery: false);

    /// <summary>Names the deployment. It tags every resource and suffixes every queue.</summary>
    public string EnvironmentName { get; }

    /// <summary>The managed runtime the function runs on.</summary>
    public string LambdaRuntimeIdentifier { get; }

    /// <summary>Memory allocated to the function.</summary>
    public int LambdaMemoryMb { get; }

    /// <summary>How long one invocation may run.</summary>
    public int LambdaTimeoutSeconds { get; }

    /// <summary>Concurrent executions the function is allowed.</summary>
    public int ReservedConcurrency { get; }

    /// <summary>Records the event source delivers per invocation.</summary>
    public int BatchSize { get; }

    /// <summary>How long the event source waits to fill a batch.</summary>
    public int BatchWindowSeconds { get; }

    /// <summary>Concurrent executions the event source may request.</summary>
    public int MaxConcurrency { get; }

    /// <summary>Operational margin added to the computed visibility timeout.</summary>
    public int VisibilityMarginSeconds { get; }

    /// <summary>Receives before a message moves to the dead-letter queue.</summary>
    public int MaxReceiveCount { get; }

    /// <summary>How long the source queue keeps a message.</summary>
    public int SourceRetentionDays { get; }

    /// <summary>How long the dead-letter queue keeps a message.</summary>
    public int DlqRetentionDays { get; }

    /// <summary>How long an idempotency record is kept.</summary>
    public int IdempotencyRetentionDays { get; }

    /// <summary>Whether the tables survive stack deletion.</summary>
    public bool RetainData { get; }

    /// <summary>Whether the tables carry point-in-time recovery.</summary>
    public bool EnablePointInTimeRecovery { get; }

    /// <summary>
    /// How long a received message stays invisible to other receivers.
    /// </summary>
    /// <remarks>
    /// Derived, never supplied. The margin is a separate term from the multiplier so that widening it
    /// does not read as a change to the AWS guidance the multiplier represents.
    /// </remarks>
    public int VisibilityTimeoutSeconds =>
        (VisibilityTimeoutMultiplier * LambdaTimeoutSeconds) + BatchWindowSeconds + VisibilityMarginSeconds;

    /// <summary>
    /// Returns the configuration for a named environment.
    /// </summary>
    /// <param name="environmentName">The value of the <c>environment</c> CDK context key.</param>
    /// <exception cref="ArgumentException">
    /// No configuration is defined for the name. The message lists the ones that are.
    /// </exception>
    /// <remarks>
    /// An unknown name fails synthesis rather than falling back to development. The failure it guards
    /// against is development sizing and retention deployed into a production account, which nothing
    /// reports until the day the retention is needed.
    /// </remarks>
    public static EnvironmentConfig ForEnvironment(string environmentName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentName);

        return string.Equals(environmentName, Development.EnvironmentName, StringComparison.OrdinalIgnoreCase)
            ? Development
            : throw new ArgumentException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"No configuration is defined for environment '{environmentName}'. "
                    + $"Defined environments: {Development.EnvironmentName}."),
                nameof(environmentName));
    }
}
