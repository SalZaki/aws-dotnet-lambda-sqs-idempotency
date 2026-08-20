using System.Globalization;
using ReliableOrders.Function.Configuration;

namespace ReliableOrders.Local;

/// <summary>
/// Everything the local stack's own program reads from its environment, checked once at start.
/// </summary>
/// <remarks>
/// <para>
/// The same shape as <see cref="FunctionConfiguration"/> and for the same reason: a missing variable
/// fails the start that reads it, naming itself, rather than the first message that needs it. The two
/// table names are read through that type's constants rather than through literals, because the
/// tables this program creates are the tables the function was told to use.
/// </para>
/// <para>
/// The endpoints are required rather than defaulted. The AWS SDK reads
/// <c>AWS_ENDPOINT_URL_SQS</c> and <c>AWS_ENDPOINT_URL_DYNAMODB</c> itself, so leaving them unset
/// would not fail — it would send every call to real AWS, on whatever credentials the machine
/// happens to carry. A local stack that quietly provisions a queue in someone's account is the one
/// failure worth making impossible.
/// </para>
/// </remarks>
internal sealed record LocalConfiguration
{
    /// <summary>The SQS emulator's endpoint.</summary>
    internal const string SqsEndpointVariable = "AWS_ENDPOINT_URL_SQS";

    /// <summary>The DynamoDB emulator's endpoint.</summary>
    internal const string DynamoDbEndpointVariable = "AWS_ENDPOINT_URL_DYNAMODB";

    /// <summary>Where the runtime interface emulator accepts an invocation.</summary>
    internal const string FunctionUrlVariable = "FUNCTION_INVOCATION_URL";

    /// <summary>Names the deployment, and so names the queue pair. Optional.</summary>
    /// <remarks>
    /// Optional here and required by the function, which is not an inconsistency. The function
    /// refuses a default because a defaulted environment mislabels every metric in an AWS account;
    /// nothing in a disposable local stack is labelled by anything but itself.
    /// </remarks>
    internal const string EnvironmentNameVariable = "ENVIRONMENT";

    /// <summary>The region queue ARNs are composed from. Optional.</summary>
    internal const string RegionVariable = "AWS_REGION";

    /// <summary>Records one invocation is given. Optional.</summary>
    internal const string BatchSizeVariable = "BATCH_SIZE";

    /// <summary>What <see cref="EnvironmentNameVariable"/> means when it is not set.</summary>
    internal const string DefaultEnvironmentName = "local";

    /// <summary>What <see cref="RegionVariable"/> means when it is not set.</summary>
    internal const string DefaultRegion = "eu-west-2";

    private LocalConfiguration(
        Uri sqsEndpoint,
        Uri dynamoDbEndpoint,
        Uri functionUrl,
        string environmentName,
        string region,
        string ordersTableName,
        string idempotencyTableName,
        int batchSize)
    {
        SqsEndpoint = sqsEndpoint;
        DynamoDbEndpoint = dynamoDbEndpoint;
        FunctionUrl = functionUrl;
        EnvironmentName = environmentName;
        Region = region;
        OrdersTableName = ordersTableName;
        IdempotencyTableName = idempotencyTableName;
        BatchSize = batchSize;
    }

    /// <summary>The SQS emulator's endpoint.</summary>
    public Uri SqsEndpoint { get; }

    /// <summary>The DynamoDB emulator's endpoint.</summary>
    public Uri DynamoDbEndpoint { get; }

    /// <summary>Where the runtime interface emulator accepts an invocation.</summary>
    public Uri FunctionUrl { get; }

    /// <summary>Names the deployment, and so names the queue pair.</summary>
    public string EnvironmentName { get; }

    /// <summary>The region queue ARNs are composed from.</summary>
    public string Region { get; }

    /// <summary>The table holding one row per order.</summary>
    public string OrdersTableName { get; }

    /// <summary>The table holding one row per event.</summary>
    public string IdempotencyTableName { get; }

    /// <summary>Records one invocation is given.</summary>
    public int BatchSize { get; }

    /// <summary>
    /// Reads the configuration from the process environment.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// A required variable is absent or blank, or a value cannot be used. The message names the
    /// variable.
    /// </exception>
    public static LocalConfiguration FromEnvironment() =>
        From(name => Environment.GetEnvironmentVariable(name));

    /// <summary>
    /// Reads the configuration from an arbitrary source, which is what lets it be tested.
    /// </summary>
    /// <param name="read">Returns a variable's value, or null when it is not set.</param>
    /// <inheritdoc cref="FromEnvironment"/>
    public static LocalConfiguration From(Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(read);

        return new LocalConfiguration(
            Endpoint(read, SqsEndpointVariable),
            Endpoint(read, DynamoDbEndpointVariable),
            Endpoint(read, FunctionUrlVariable),
            Optional(read, EnvironmentNameVariable) ?? DefaultEnvironmentName,
            Optional(read, RegionVariable) ?? DefaultRegion,
            Required(read, FunctionConfiguration.OrdersTableNameVariable),
            Required(read, FunctionConfiguration.IdempotencyTableNameVariable),
            Batched(read));
    }

    private static string? Optional(Func<string, string?> read, string name)
    {
        var value = read(name);

        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string Required(Func<string, string?> read, string name) =>
        Optional(read, name)
        ?? throw new InvalidOperationException($"{name} is not set. It has no default and must be configured.");

    /// <remarks>
    /// <para>
    /// Parsed rather than carried as a string, so a value that is not a URL fails here naming the
    /// variable rather than inside an SDK client that reports it as a malformed service URL.
    /// </para>
    /// <para>
    /// Absolute is not a strong enough test, which is the whole reason the scheme and host are
    /// checked. <c>Uri.TryCreate</c> reads <c>sqs:4566</c> as an absolute URI with the scheme
    /// <c>sqs</c> and the path <c>4566</c>, and on Unix it reads a bare path as a file URI. Both are
    /// what someone copying a host and a port out of the Compose file writes, and both would be
    /// accepted here and reach the SDK as a service URL resolving to nothing.
    /// </para>
    /// </remarks>
    private static Uri Endpoint(Func<string, string?> read, string name)
    {
        var value = Required(read, name);

        return Uri.TryCreate(value, UriKind.Absolute, out var endpoint)
            && (string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)
                || string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
            && !string.IsNullOrEmpty(endpoint.Host)
            ? endpoint
            : throw new InvalidOperationException(
                $"{name} is set to '{value}', which is not an http or https URL with a host.");
    }

    /// <remarks>
    /// The upper bound is SQS's own: a single receive returns at most ten messages, so a larger batch
    /// size would be silently clamped and the stack would demonstrate a batch nobody configured.
    /// </remarks>
    private static int Batched(Func<string, string?> read)
    {
        var value = Optional(read, BatchSizeVariable);

        if (value is null)
        {
            return MaximumBatchSize;
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            throw new InvalidOperationException($"{BatchSizeVariable} is set to '{value}', which is not a number.");
        }

        return parsed is >= 1 and <= MaximumBatchSize
            ? parsed
            : throw new InvalidOperationException(
                $"{BatchSizeVariable} is set to '{value}', and one receive returns between 1 and "
                + $"{MaximumBatchSize} messages.");
    }

    /// <summary>
    /// The most messages one <c>ReceiveMessage</c> call returns.
    /// </summary>
    private const int MaximumBatchSize = 10;
}
