using System.Globalization;
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
        // The raised timeout carries the oldest-message-age threshold with it. Leaving the default 300
        // against this 302 would fail the pairing rule rather than the assertion below.
        var config = Config(
            lambdaTimeoutSeconds: 45,
            batchWindowSeconds: 2,
            visibilityMarginSeconds: 30,
            alarmThresholds: Thresholds(oldestMessageAgeSeconds: 600));

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
    /// A batch larger than SQS delivers without a batching window is rejected.
    /// </summary>
    /// <remarks>
    /// CDK stops checking the ceiling as soon as a window is defined, and a window of zero seconds is
    /// defined — so the oversized batch synthesises and CloudFormation rejects it at deploy. Ten is
    /// accepted at zero, and the same batch is accepted once there is a window to fill it.
    /// </remarks>
    [Fact]
    public void A_batch_larger_than_ten_is_rejected_without_a_batching_window()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => Config(batchSize: 11, batchWindowSeconds: 0));

        Assert.Contains("11", exception.Message, StringComparison.Ordinal);
        Assert.Contains("10", exception.Message, StringComparison.Ordinal);

        Assert.Equal(10, Config(batchSize: 10, batchWindowSeconds: 0).BatchSize);
        Assert.Equal(11, Config(batchSize: 11, batchWindowSeconds: 1).BatchSize);
    }

    /// <summary>
    /// The bounds SQS puts on the event source are refused here rather than at deploy.
    /// </summary>
    /// <remarks>
    /// A maximum concurrency of one is the one worth naming. It reads as "serialise the consumer" and
    /// is illegal, so the deployment that meant to slow the service down is the one that fails.
    /// </remarks>
    [Theory]
    [InlineData(1)]
    [InlineData(1001)]
    public void An_event_source_concurrency_outside_the_accepted_range_is_rejected(int maxConcurrency)
    {
        var exception = Assert.Throws<ArgumentException>(
            () => Config(maxConcurrency: maxConcurrency, reservedConcurrency: 2000));

        Assert.Contains(
            maxConcurrency.ToString(CultureInfo.InvariantCulture),
            exception.Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A batching window longer than SQS waits is refused.
    /// </summary>
    [Fact]
    public void A_batching_window_longer_than_five_minutes_is_rejected()
    {
        var exception = Assert.Throws<ArgumentException>(() => Config(batchWindowSeconds: 301));

        Assert.Contains("301", exception.Message, StringComparison.Ordinal);
        Assert.Contains("300", exception.Message, StringComparison.Ordinal);
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
    /// The oldest-message-age threshold has to clear the visibility timeout it is measured beside.
    /// </summary>
    /// <remarks>
    /// Equal is rejected as well as shorter, for the reason the retention rule rejects equal: a message
    /// that failed one receive and is waiting out its visibility timeout has aged exactly that far
    /// while behaving correctly. The development pairing is 300 against 210.
    /// </remarks>
    [Theory]
    [InlineData(210)]
    [InlineData(120)]
    public void An_oldest_message_age_threshold_within_the_visibility_timeout_is_rejected(int seconds)
    {
        var exception = Assert.Throws<ArgumentException>(
            () => Config(alarmThresholds: Thresholds(oldestMessageAgeSeconds: seconds)));

        Assert.Contains(
            seconds.ToString(CultureInfo.InvariantCulture),
            exception.Message,
            StringComparison.Ordinal);

        Assert.Contains("210", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The transient-failure threshold has to clear the retries one message is allowed.
    /// </summary>
    /// <remarks>
    /// Transient failures are not gated on first receipt, so a single poison message emits one sample
    /// per attempt and a threshold at the receive count turns that message into an alarm. Equal is the
    /// value someone reaches by reading the two numbers as the same quantity.
    /// </remarks>
    [Theory]
    [InlineData(5)]
    [InlineData(3)]
    public void A_transient_failure_threshold_within_the_receive_count_is_rejected(int failures)
    {
        var exception = Assert.Throws<ArgumentException>(
            () => Config(
                maxReceiveCount: 5,
                alarmThresholds: Thresholds(transientFailuresPerFiveMinutes: failures)));

        Assert.Contains(
            failures.ToString(CultureInfo.InvariantCulture),
            exception.Message,
            StringComparison.Ordinal);

        Assert.Contains("5", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The development thresholds satisfy both rules, so the defaults are not a combination the record
    /// would refuse.
    /// </summary>
    [Fact]
    public void The_development_thresholds_clear_both_cross_value_rules()
    {
        var config = EnvironmentConfig.Development;

        Assert.True(config.AlarmThresholds.OldestMessageAgeSeconds > config.VisibilityTimeoutSeconds);
        Assert.True(config.AlarmThresholds.TransientFailuresPerFiveMinutes > config.MaxReceiveCount);
    }

    /// <summary>
    /// A no-progress window that is not a whole number of aggregation periods is rejected.
    /// </summary>
    /// <remarks>
    /// The window deploys as a count of five-minute periods, so a remainder is discarded rather than
    /// rounded. Twelve would watch ten minutes, and anything below five would deploy zero evaluation
    /// periods, which CloudWatch does not accept. Neither reports itself: the alarm exists, shows a
    /// state, and covers a window nobody chose.
    /// </remarks>
    [Theory]
    [InlineData(12)]
    [InlineData(4)]
    [InlineData(1)]
    public void A_no_progress_window_that_is_not_a_whole_number_of_periods_is_rejected(int minutes)
    {
        var exception = Assert.Throws<ArgumentException>(
            () => Thresholds(noProgressMinutes: minutes));

        Assert.Contains(
            minutes.ToString(CultureInfo.InvariantCulture),
            exception.Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A threshold of zero deploys an alarm that cannot distinguish a fault from an idle queue.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_threshold_that_is_not_positive_is_rejected(int value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Thresholds(deadlineDeferralsPerFiveMinutes: value));
    }

    /// <summary>
    /// An endpoint SNS would refuse is refused here instead.
    /// </summary>
    /// <remarks>
    /// The subscription is created with the stack, and SNS rejects a malformed address at subscribe
    /// time rather than at deploy. The stack reports success, every alarm is wired to a topic with no
    /// confirmed subscriber, and the first thing that says so is the incident nobody was paged for.
    /// </remarks>
    [Theory]
    [InlineData("not-an-address")]
    [InlineData("alerts at example.invalid")]
    [InlineData("alerts@reliable orders.invalid")]
    public void An_alarm_endpoint_that_is_not_an_address_is_rejected(string endpoint)
    {
        var exception = Assert.Throws<ArgumentException>(() => Config(alarmEndpoint: endpoint));

        Assert.Contains(endpoint, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The development endpoint is a reserved domain, so a clone of a public repository cannot mail a
    /// real person.
    /// </summary>
    [Fact]
    public void The_development_endpoint_cannot_reach_a_real_mailbox()
    {
        Assert.EndsWith(".invalid", EnvironmentConfig.Development.AlarmEndpoint, StringComparison.Ordinal);
    }

    /// <summary>
    /// The development defaults with one value replaced, so each case states only what it is about.
    /// </summary>
    private static AlarmThresholds Thresholds(
        int oldestMessageAgeSeconds = 300,
        int throttleEvaluationMinutes = 3,
        int transientFailuresPerFiveMinutes = 10,
        int noProgressMinutes = 15,
        int deadlineDeferralsPerFiveMinutes = 1) =>
        new(
            oldestMessageAgeSeconds: oldestMessageAgeSeconds,
            throttleEvaluationMinutes: throttleEvaluationMinutes,
            transientFailuresPerFiveMinutes: transientFailuresPerFiveMinutes,
            noProgressMinutes: noProgressMinutes,
            deadlineDeferralsPerFiveMinutes: deadlineDeferralsPerFiveMinutes);

    /// <summary>
    /// The development defaults with one or two values replaced, so each case states only what it is
    /// about.
    /// </summary>
    private static EnvironmentConfig Config(
        int batchSize = 10,
        int lambdaTimeoutSeconds = 30,
        int reservedConcurrency = 10,
        int batchWindowSeconds = 1,
        int maxConcurrency = 10,
        int visibilityMarginSeconds = 29,
        int maxReceiveCount = 5,
        int sourceRetentionDays = 4,
        int dlqRetentionDays = 14,
        AlarmThresholds? alarmThresholds = null,
        string alarmEndpoint = "alerts@reliable-orders.invalid") =>
        new(
            environmentName: "dev",
            lambdaRuntimeIdentifier: "dotnet10",
            lambdaMemoryMb: 512,
            lambdaTimeoutSeconds: lambdaTimeoutSeconds,
            reservedConcurrency: reservedConcurrency,
            batchSize: batchSize,
            batchWindowSeconds: batchWindowSeconds,
            maxConcurrency: maxConcurrency,
            visibilityMarginSeconds: visibilityMarginSeconds,
            maxReceiveCount: maxReceiveCount,
            sourceRetentionDays: sourceRetentionDays,
            dlqRetentionDays: dlqRetentionDays,
            idempotencyRetentionDays: 30,
            retainData: false,
            enablePointInTimeRecovery: false,
            alarmThresholds: alarmThresholds ?? Thresholds(),
            alarmEndpoint: alarmEndpoint);
}
