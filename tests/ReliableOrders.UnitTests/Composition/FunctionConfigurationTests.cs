using Microsoft.Extensions.Logging;
using ReliableOrders.Core.Idempotency;
using ReliableOrders.Core.Validation;
using ReliableOrders.Function.Configuration;

namespace ReliableOrders.UnitTests.Composition;

/// <summary>
/// What a cold start does with the environment it was given.
/// </summary>
/// <remarks>
/// The story's criterion is that missing configuration fails initialisation with a message naming the
/// variable, so every failing case asserts on the message. A test satisfied by "it threw" would pass
/// against an exception that leaves an operator no better off than an unexplained crash.
/// </remarks>
public sealed class FunctionConfigurationTests
{
    [Fact]
    public void A_complete_environment_is_read()
    {
        var configuration = FunctionConfiguration.From(Complete());

        Assert.Equal("orders", configuration.OrdersTableName);
        Assert.Equal("idempotency", configuration.IdempotencyTableName);
        Assert.Equal("reliable-orders", configuration.ServiceName);
        Assert.Equal("test", configuration.Environment);
        Assert.Equal("ReliableOrders", configuration.MetricsNamespace);
    }

    /// <summary>
    /// Every required variable names itself when absent.
    /// </summary>
    [Theory]
    [InlineData(FunctionConfiguration.OrdersTableNameVariable)]
    [InlineData(FunctionConfiguration.IdempotencyTableNameVariable)]
    [InlineData(FunctionConfiguration.ServiceNameVariable)]
    [InlineData(FunctionConfiguration.EnvironmentVariable)]
    [InlineData(FunctionConfiguration.MetricsNamespaceVariable)]
    public void A_missing_required_variable_names_itself(string missing)
    {
        var failure = Assert.Throws<InvalidOperationException>(
            () => FunctionConfiguration.From(Complete(without: missing)));

        Assert.Contains(missing, failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Blank is missing.
    /// </summary>
    /// <remarks>
    /// An environment variable set to an empty string is what a template with an unresolved
    /// substitution produces, and it is exactly as unusable as one never set.
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_required_variable_is_treated_as_missing(string value)
    {
        var failure = Assert.Throws<InvalidOperationException>(
            () => FunctionConfiguration.From(Complete(with: (FunctionConfiguration.OrdersTableNameVariable, value))));

        Assert.Contains(FunctionConfiguration.OrdersTableNameVariable, failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The optional values fall back to what the specification chose.
    /// </summary>
    [Fact]
    public void Absent_optional_variables_use_their_documented_defaults()
    {
        var configuration = FunctionConfiguration.From(Complete());

        Assert.Equal(IdempotencyRetention.Default.Duration, configuration.Retention.Duration);
        Assert.Equal(EventSkewWindow.Default.MaxFuture, configuration.SkewWindow.MaxFuture);
        Assert.Equal(EventSkewWindow.Default.MaxPast, configuration.SkewWindow.MaxPast);
        Assert.Equal(LogLevel.Information, configuration.LogLevel);
    }

    [Fact]
    public void The_optional_values_are_read_when_set()
    {
        var configuration = FunctionConfiguration.From(Complete(
            with: (FunctionConfiguration.IdempotencyRetentionDaysVariable, "7"),
            and: (FunctionConfiguration.LogLevelVariable, "Warning")));

        Assert.Equal(TimeSpan.FromDays(7), configuration.Retention.Duration);
        Assert.Equal(LogLevel.Warning, configuration.LogLevel);
    }

    /// <summary>
    /// A value someone set and got wrong fails rather than falling back.
    /// </summary>
    /// <remarks>
    /// Defaulting over an unparseable value would run the service on a number nobody chose, and the
    /// deployment that set it would look like it had taken effect.
    /// </remarks>
    [Theory]
    [InlineData(FunctionConfiguration.IdempotencyRetentionDaysVariable, "soon")]
    [InlineData(FunctionConfiguration.MaxEventSkewFutureHoursVariable, "")]
    [InlineData(FunctionConfiguration.MaxEventSkewPastDaysVariable, "a week")]
    [InlineData(FunctionConfiguration.LogLevelVariable, "Chatty")]
    public void An_unusable_optional_value_names_its_variable(string name, string value)
    {
        if (value.Length == 0)
        {
            // An empty optional is absent, not wrong, so this case asserts the opposite.
            Assert.NotNull(FunctionConfiguration.From(Complete(with: (name, value))));

            return;
        }

        var failure = Assert.Throws<InvalidOperationException>(
            () => FunctionConfiguration.From(Complete(with: (name, value))));

        Assert.Contains(name, failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A value outside the bounds its type allows names the variable, not a constructor parameter.
    /// </summary>
    /// <remarks>
    /// <see cref="IdempotencyRetention"/> rejects a duration beyond its maximum with an
    /// <see cref="ArgumentException"/> naming a parameter no operator has heard of. What reaches the
    /// log has to name <c>IDEMPOTENCY_RETENTION_DAYS</c>, with the original kept underneath so the
    /// bound itself is not lost.
    /// </remarks>
    [Fact]
    public void A_value_out_of_range_names_its_variable_and_keeps_the_cause()
    {
        var failure = Assert.Throws<InvalidOperationException>(() => FunctionConfiguration.From(
            Complete(with: (FunctionConfiguration.IdempotencyRetentionDaysVariable, "100000"))));

        Assert.Contains(FunctionConfiguration.IdempotencyRetentionDaysVariable, failure.Message, StringComparison.Ordinal);
        Assert.IsAssignableFrom<ArgumentException>(failure.InnerException);
    }

    /// <summary>
    /// The two tables must differ, and the message names both variables.
    /// </summary>
    /// <remarks>
    /// Refused while reading the environment rather than left to <c>DynamoDbTableNames</c>, whose own
    /// check reports an <see cref="ArgumentException"/> naming a constructor parameter. One table for
    /// both rows is a plausible copy-and-paste between two variables, so the message has to name the
    /// two an operator would go and edit.
    /// </remarks>
    [Fact]
    public void The_two_tables_may_not_be_the_same()
    {
        var failure = Assert.Throws<InvalidOperationException>(() => FunctionConfiguration.From(
            Complete(with: (FunctionConfiguration.IdempotencyTableNameVariable, "orders"))));

        Assert.Contains(FunctionConfiguration.OrdersTableNameVariable, failure.Message, StringComparison.Ordinal);
        Assert.Contains(
            FunctionConfiguration.IdempotencyTableNameVariable,
            failure.Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A number too large for the duration it becomes is out of range, not a crash.
    /// </summary>
    /// <remarks>
    /// <c>double.TryParse</c> accepts <c>1e30</c> and <c>Infinity</c>, and <c>TimeSpan.FromDays</c>
    /// answers both with an <see cref="OverflowException"/> — which is not an
    /// <see cref="ArgumentException"/>, so it escaped naming nothing. <c>NaN</c> is the same story
    /// through a different exception.
    /// </remarks>
    [Theory]
    [InlineData(FunctionConfiguration.IdempotencyRetentionDaysVariable, "1e30")]
    [InlineData(FunctionConfiguration.IdempotencyRetentionDaysVariable, "Infinity")]
    [InlineData(FunctionConfiguration.MaxEventSkewFutureHoursVariable, "NaN")]
    [InlineData(FunctionConfiguration.MaxEventSkewPastDaysVariable, "999999999999")]
    public void A_number_the_duration_cannot_hold_names_its_variable(string name, string value)
    {
        var failure = Assert.Throws<InvalidOperationException>(
            () => FunctionConfiguration.From(Complete(with: (name, value))));

        Assert.Contains(name, failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An out-of-range past bound is reported against the past variable.
    /// </summary>
    /// <remarks>
    /// With both set, the failure used to name the future variable and quote its value — which was
    /// correct — sending an operator to change the one thing that was not wrong while the cold start
    /// kept failing.
    /// </remarks>
    [Fact]
    public void An_out_of_range_skew_names_the_variable_it_came_from()
    {
        var failure = Assert.Throws<InvalidOperationException>(() => FunctionConfiguration.From(Complete(
            with: (FunctionConfiguration.MaxEventSkewFutureHoursVariable, "1"),
            and: (FunctionConfiguration.MaxEventSkewPastDaysVariable, "999999"))));

        Assert.Contains(FunctionConfiguration.MaxEventSkewPastDaysVariable, failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A log level that is not a level, or that silences the service, is refused.
    /// </summary>
    /// <remarks>
    /// <c>Enum.TryParse</c> accepts a number, so <c>6</c> became <c>None</c> and <c>99</c> an
    /// undefined value — each of which makes the minimum level suppress every line the service writes.
    /// A mistyped variable would leave a function running with no observability and nothing to point
    /// at, which is what this type exists to prevent.
    /// </remarks>
    [Theory]
    [InlineData("6")]
    [InlineData("99")]
    [InlineData("-1")]
    [InlineData("None")]
    [InlineData("Chatty")]
    public void A_log_level_that_would_silence_the_service_is_refused(string value)
    {
        var failure = Assert.Throws<InvalidOperationException>(
            () => FunctionConfiguration.From(Complete(with: (FunctionConfiguration.LogLevelVariable, value))));

        Assert.Contains(FunctionConfiguration.LogLevelVariable, failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A complete environment, optionally missing one variable or overriding others.
    /// </summary>
    private static Func<string, string?> Complete(
        string? without = null,
        (string Name, string Value)? with = null,
        (string Name, string Value)? and = null)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [FunctionConfiguration.OrdersTableNameVariable] = "orders",
            [FunctionConfiguration.IdempotencyTableNameVariable] = "idempotency",
            [FunctionConfiguration.ServiceNameVariable] = "reliable-orders",
            [FunctionConfiguration.EnvironmentVariable] = "test",
            [FunctionConfiguration.MetricsNamespaceVariable] = "ReliableOrders",
        };

        if (without is not null)
        {
            values.Remove(without);
        }

        if (with is not null)
        {
            values[with.Value.Name] = with.Value.Value;
        }

        if (and is not null)
        {
            values[and.Value.Name] = and.Value.Value;
        }

        return name => values.TryGetValue(name, out var value) ? value : null;
    }
}
