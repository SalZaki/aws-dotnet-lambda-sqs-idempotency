using ReliableOrders.Cdk.Configuration;

namespace ReliableOrders.CdkTests.Configuration;

/// <summary>
/// The derived visibility timeout and the invariants the record refuses to be built without.
/// </summary>
/// <remarks>
/// Each rejected combination is one CloudFormation accepts. The deployment succeeds and the damage
/// surfaces later, under load for the concurrency rule and during a dead-letter investigation for the
/// retention rule, which is why the check is code rather than a review checklist.
/// </remarks>
public sealed class EnvironmentConfigTests
{
    /// <summary>
    /// The worked example from docs/infrastructure.md, (6 × 30) + 1 + 29.
    /// </summary>
    [Fact]
    public void The_development_defaults_evaluate_to_a_210_second_visibility_timeout()
    {
        Assert.Equal(210, EnvironmentConfig.Development.VisibilityTimeoutSeconds);
    }

    /// <summary>
    /// The timeout moves with the values it is derived from rather than staying at the documented 210.
    /// </summary>
    [Fact]
    public void The_visibility_timeout_follows_the_function_timeout_window_and_margin()
    {
        var config = Config(lambdaTimeoutSeconds: 45, batchWindowSeconds: 2, visibilityMarginSeconds: 30);

        Assert.Equal((6 * 45) + 2 + 30, config.VisibilityTimeoutSeconds);
    }

    /// <summary>
    /// The event source cannot be allowed to request more concurrency than the function may use.
    /// </summary>
    [Fact]
    public void Maximum_concurrency_above_reserved_concurrency_is_rejected()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => Config(reservedConcurrency: 10, maxConcurrency: 11));

        Assert.Contains("10", exception.Message, StringComparison.Ordinal);
        Assert.Contains("11", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Equal retention is rejected as well as shorter retention.
    /// </summary>
    /// <remarks>
    /// Equal retention leaves an operator no more time to diagnose the message than it already spent
    /// failing, and equal is the value someone reaches by copying the source setting.
    /// </remarks>
    [Theory]
    [InlineData(4)]
    [InlineData(3)]
    public void Dead_letter_retention_that_does_not_exceed_source_retention_is_rejected(int dlqRetentionDays)
    {
        var exception = Assert.Throws<ArgumentException>(
            () => Config(sourceRetentionDays: 4, dlqRetentionDays: dlqRetentionDays));

        Assert.Contains("4", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Five receives, the documented floor.
    /// </summary>
    [Fact]
    public void A_maximum_receive_count_below_five_is_rejected()
    {
        var exception = Assert.Throws<ArgumentException>(() => Config(maxReceiveCount: 4));

        Assert.Contains("4", exception.Message, StringComparison.Ordinal);
        Assert.Contains("5", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Five is accepted, so the rule reads as a floor rather than as "more than five".
    /// </summary>
    [Fact]
    public void A_maximum_receive_count_of_five_is_accepted()
    {
        Assert.Equal(5, Config(maxReceiveCount: 5).MaxReceiveCount);
    }

    /// <summary>
    /// The context value is resolved to a configuration.
    /// </summary>
    [Theory]
    [InlineData("dev")]
    [InlineData("DEV")]
    public void A_known_environment_name_resolves_to_its_configuration(string environmentName)
    {
        Assert.Same(EnvironmentConfig.Development, EnvironmentConfig.ForEnvironment(environmentName));
    }

    /// <summary>
    /// An unknown name fails synthesis rather than falling back to development sizing.
    /// </summary>
    [Fact]
    public void An_unknown_environment_name_is_rejected_naming_the_ones_that_exist()
    {
        var exception = Assert.Throws<ArgumentException>(() => EnvironmentConfig.ForEnvironment("prod"));

        Assert.Contains("prod", exception.Message, StringComparison.Ordinal);
        Assert.Contains("dev", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The development defaults with one or two values replaced, so each case states only what it is
    /// about.
    /// </summary>
    private static EnvironmentConfig Config(
        int lambdaTimeoutSeconds = 30,
        int reservedConcurrency = 10,
        int batchWindowSeconds = 1,
        int maxConcurrency = 10,
        int visibilityMarginSeconds = 29,
        int maxReceiveCount = 5,
        int sourceRetentionDays = 4,
        int dlqRetentionDays = 14) =>
        new(
            environmentName: "dev",
            lambdaRuntimeIdentifier: "dotnet10",
            lambdaMemoryMb: 512,
            lambdaTimeoutSeconds: lambdaTimeoutSeconds,
            reservedConcurrency: reservedConcurrency,
            batchSize: 10,
            batchWindowSeconds: batchWindowSeconds,
            maxConcurrency: maxConcurrency,
            visibilityMarginSeconds: visibilityMarginSeconds,
            maxReceiveCount: maxReceiveCount,
            sourceRetentionDays: sourceRetentionDays,
            dlqRetentionDays: dlqRetentionDays,
            idempotencyRetentionDays: 30,
            retainData: false,
            enablePointInTimeRecovery: false);
}
