using ReliableOrders.Function.Configuration;
using ReliableOrders.Local;

namespace ReliableOrders.UnitTests.Local;

/// <summary>
/// What the local development stack's own program does with the environment it was given.
/// </summary>
/// <remarks>
/// <para>
/// The same shape as <see cref="Composition.FunctionConfigurationTests"/>, and for the same reason: every failing
/// case asserts on the message, because a test satisfied by "it threw" passes against an exception
/// that leaves the reader no better off than an unexplained crash. The stack is started by someone
/// who has not read this program, and a container that exits naming nothing is where they stop.
/// </para>
/// <para>
/// Here rather than beside the stack's parity cases in the CDK suite, which need a synthesised
/// template and these do not. Nothing below starts a container or touches Docker.
/// </para>
/// </remarks>
public sealed class LocalConfigurationTests
{
    [Fact]
    public void A_complete_environment_is_read()
    {
        var configuration = LocalConfiguration.From(Complete());

        Assert.Equal(new Uri("http://sqs:4566"), configuration.SqsEndpoint);
        Assert.Equal(new Uri("http://dynamodb:8000"), configuration.DynamoDbEndpoint);
        Assert.Equal(new Uri("http://function:8080/invocations"), configuration.FunctionUrl);
        Assert.Equal("orders", configuration.OrdersTableName);
        Assert.Equal("idempotency", configuration.IdempotencyTableName);
    }

    /// <summary>
    /// Every required variable names itself when absent.
    /// </summary>
    /// <remarks>
    /// The endpoints are required rather than defaulted, and that is the case worth keeping. The AWS
    /// SDK reads the same two variables itself, so an absent one does not fail — it resolves the real
    /// service on whatever credentials the machine carries, and a local stack that quietly provisions
    /// a queue in someone's account is the failure worth making impossible.
    /// </remarks>
    [Theory]
    [InlineData(LocalConfiguration.SqsEndpointVariable)]
    [InlineData(LocalConfiguration.DynamoDbEndpointVariable)]
    [InlineData(LocalConfiguration.FunctionUrlVariable)]
    [InlineData(FunctionConfiguration.OrdersTableNameVariable)]
    [InlineData(FunctionConfiguration.IdempotencyTableNameVariable)]
    public void A_missing_required_variable_names_itself(string missing)
    {
        var failure = Assert.Throws<InvalidOperationException>(
            () => LocalConfiguration.From(Complete(without: missing)));

        Assert.Contains(missing, failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Blank is missing, for a required variable and for an optional one alike.
    /// </summary>
    /// <remarks>
    /// An empty string is what a Compose file with an unresolved substitution produces, and it is
    /// exactly as unusable as a variable never set. The optional case matters more than it looks: a
    /// blank <c>ENVIRONMENT</c> that fell through as a name would build the queue
    /// <c>reliable-orders-</c>, which SQS accepts.
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_variable_is_treated_as_missing(string blank)
    {
        var failure = Assert.Throws<InvalidOperationException>(
            () => LocalConfiguration.From(Complete(with: (LocalConfiguration.SqsEndpointVariable, blank))));

        Assert.Contains(LocalConfiguration.SqsEndpointVariable, failure.Message, StringComparison.Ordinal);

        var configuration = LocalConfiguration.From(
            Complete(with: (LocalConfiguration.EnvironmentNameVariable, blank)));

        Assert.Equal(LocalConfiguration.DefaultEnvironmentName, configuration.EnvironmentName);
    }

    /// <summary>
    /// An endpoint that is not an absolute URL names the variable and quotes what it was given.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Parsed here rather than left to the SDK, which reports it as a malformed service URL from
    /// inside a client, naming neither the variable nor the file it was set in.
    /// </para>
    /// <para>
    /// The first two cases are why this asserts on more than "absolute". <c>Uri.TryCreate</c> reads
    /// <c>sqs:4566</c> as a URI with the scheme <c>sqs</c>, and on Unix it reads a bare path as a
    /// file URI, so both passed an absolute-URI check and reached the SDK as an endpoint resolving to
    /// nothing. A host and port with no scheme is the mistake a reader makes copying one out of the
    /// Compose file, which is the one worth catching here.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("sqs:4566")]
    [InlineData("not a url")]
    [InlineData("/queues")]
    public void An_endpoint_that_is_not_an_absolute_url_is_refused(string value)
    {
        var failure = Assert.Throws<InvalidOperationException>(
            () => LocalConfiguration.From(Complete(with: (LocalConfiguration.SqsEndpointVariable, value))));

        Assert.Contains(LocalConfiguration.SqsEndpointVariable, failure.Message, StringComparison.Ordinal);
        Assert.Contains(value, failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The optional values fall back to what this program chose.
    /// </summary>
    [Fact]
    public void Absent_optional_variables_use_their_documented_defaults()
    {
        var configuration = LocalConfiguration.From(Complete());

        Assert.Equal(LocalConfiguration.DefaultEnvironmentName, configuration.EnvironmentName);
        Assert.Equal(LocalConfiguration.DefaultRegion, configuration.Region);
        Assert.Equal(MaximumBatchSize, configuration.BatchSize);
    }

    [Fact]
    public void The_optional_values_are_read_when_set()
    {
        var configuration = LocalConfiguration.From(Complete(
            with: (LocalConfiguration.EnvironmentNameVariable, "demo"),
            and: (LocalConfiguration.RegionVariable, "us-east-1")));

        Assert.Equal("demo", configuration.EnvironmentName);
        Assert.Equal("us-east-1", configuration.Region);
    }

    /// <summary>
    /// A batch size that is not a number names its variable.
    /// </summary>
    [Theory]
    [InlineData("ten")]
    [InlineData("3.5")]
    [InlineData("")]
    public void A_batch_size_that_is_not_a_whole_number_is_refused_or_defaulted(string value)
    {
        var read = Complete(with: (LocalConfiguration.BatchSizeVariable, value));

        // Blank is the one value that is not an error, because blank is how a variable says it was
        // never set. The other two are a number this program cannot use, and each names itself.
        if (value.Length == 0)
        {
            Assert.Equal(MaximumBatchSize, LocalConfiguration.From(read).BatchSize);

            return;
        }

        var failure = Assert.Throws<InvalidOperationException>(() => LocalConfiguration.From(read));

        Assert.Contains(LocalConfiguration.BatchSizeVariable, failure.Message, StringComparison.Ordinal);
        Assert.Contains(value, failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A batch size outside what one receive returns is refused rather than clamped.
    /// </summary>
    /// <remarks>
    /// SQS returns at most ten messages per call, so a larger number is silently clamped by the
    /// service and the stack would demonstrate a batch nobody configured. Zero and below is the
    /// opposite mistake and would poll forever returning nothing.
    /// </remarks>
    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("11")]
    public void A_batch_size_outside_what_one_receive_returns_is_refused(string value)
    {
        var failure = Assert.Throws<InvalidOperationException>(
            () => LocalConfiguration.From(Complete(with: (LocalConfiguration.BatchSizeVariable, value))));

        Assert.Contains(LocalConfiguration.BatchSizeVariable, failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Both ends of the accepted range are accepted.
    /// </summary>
    /// <remarks>
    /// The bounds rather than a value between them, because an inclusive check written exclusively
    /// passes every test that only asks about the middle.
    /// </remarks>
    [Theory]
    [InlineData("1", 1)]
    [InlineData("10", MaximumBatchSize)]
    public void A_batch_size_at_either_bound_is_accepted(string value, int expected)
    {
        var configuration = LocalConfiguration.From(Complete(with: (LocalConfiguration.BatchSizeVariable, value)));

        Assert.Equal(expected, configuration.BatchSize);
    }

    [Fact]
    public void Reading_from_nothing_is_refused()
    {
        Assert.Throws<ArgumentNullException>(() => LocalConfiguration.From(null!));
    }

    /// <summary>
    /// The most messages one <c>ReceiveMessage</c> call returns, which is also the default.
    /// </summary>
    private const int MaximumBatchSize = 10;

    /// <summary>
    /// An environment with every required variable set, which each case then breaks in one way.
    /// </summary>
    private static Func<string, string?> Complete(
        string? without = null,
        (string Name, string Value)? with = null,
        (string Name, string Value)? and = null)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [LocalConfiguration.SqsEndpointVariable] = "http://sqs:4566",
            [LocalConfiguration.DynamoDbEndpointVariable] = "http://dynamodb:8000",
            [LocalConfiguration.FunctionUrlVariable] = "http://function:8080/invocations",
            [FunctionConfiguration.OrdersTableNameVariable] = "orders",
            [FunctionConfiguration.IdempotencyTableNameVariable] = "idempotency",
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
