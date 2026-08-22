using System.Globalization;
using System.Text.Json;
using Amazon.CloudWatch;
using Amazon.CloudWatchLogs;
using Amazon.DynamoDBv2;
using Amazon.SQS;

namespace ReliableOrders.EndToEndTests;

/// <summary>
/// The deployment these tests run against, as it described itself on the way up.
/// </summary>
/// <remarks>
/// <para>
/// Everything is read from the outputs file <c>cdk deploy --outputs-file</c> wrote, named by an
/// environment variable. The alternative was asking CloudFormation, which means a permission granted
/// for the length of one lookup and a role that can describe stacks it has no other business with —
/// and the file is what <c>check-stack-outputs.py</c> already reads, so a name that stopped being
/// published fails the deployment before it reaches a test.
/// </para>
/// <para>
/// Nothing here creates or destroys anything. The workflow deploys the stack, points these tests at
/// it, and destroys it in a step that runs whether they passed or not. A fixture that deployed its
/// own would leave one behind on every run it did not finish.
/// </para>
/// </remarks>
public sealed class Deployment : IDisposable
{
    /// <summary>
    /// The file <c>cdk deploy --outputs-file</c> wrote.
    /// </summary>
    public const string OutputsFileVariable = "E2E_OUTPUTS_FILE";

    /// <summary>
    /// Which stack in it, since the file is keyed by stack name.
    /// </summary>
    public const string StackNameVariable = "E2E_STACK_NAME";

    /// <summary>
    /// Whether there is a stack to test at all.
    /// </summary>
    /// <remarks>
    /// Read once per process rather than per test. It is a question about the machine the suite is
    /// running on, and asking it eight times invites eight answers.
    /// </remarks>
    public static bool IsConfigured { get; } = Configured();

    /// <summary>
    /// What a skipped test says, so the reason is not "false".
    /// </summary>
    public const string SkipReason =
        "No deployed stack. Set E2E_OUTPUTS_FILE to the file cdk deploy --outputs-file wrote and "
        + "E2E_STACK_NAME to the stack in it.";

    /// <summary>
    /// Opens nothing.
    /// </summary>
    /// <remarks>
    /// Everything below is behind a <see cref="Lazy{T}"/>, and that is not a performance decision.
    /// xUnit builds a class fixture before it decides that every test in the class is skipped, so a
    /// constructor that read the outputs would fail the run on a machine with nothing deployed —
    /// which is the machine the skip exists for.
    /// </remarks>
    public Deployment()
    {
        _outputs = new Lazy<Dictionary<string, string>>(Outputs);
        _sqs = new Lazy<IAmazonSQS>(() => new AmazonSQSClient());
        _dynamoDb = new Lazy<IAmazonDynamoDB>(() => new AmazonDynamoDBClient());
        _logs = new Lazy<IAmazonCloudWatchLogs>(() => new AmazonCloudWatchLogsClient());
        _metrics = new Lazy<IAmazonCloudWatch>(() => new AmazonCloudWatchClient());
    }

    /// <summary>The queue orders are published to.</summary>
    public string SourceQueueUrl => Required("SourceQueueUrl");

    /// <summary>The queue messages reach when their receives run out.</summary>
    public string DeadLetterQueueUrl => Required("DeadLetterQueueUrl");

    /// <summary>The table holding one row per order.</summary>
    public string OrdersTableName => Required("OrdersTableName");

    /// <summary>The table holding one row per event.</summary>
    public string IdempotencyTableName => Required("IdempotencyRecordsTableName");

    /// <summary>The function the event source invokes.</summary>
    public string FunctionName => Required("OrderProcessorFunctionName");

    /// <summary>Where the function writes its structured lines.</summary>
    public string LogGroupName => Required("OrderProcessorLogGroupName");

    /// <summary>
    /// The environment the stack was deployed under, which is a metric dimension.
    /// </summary>
    /// <remarks>
    /// Taken from the stack name rather than asked for as a third variable. The stack is
    /// <c>ReliableOrders-&lt;environment&gt;</c> by construction, and a run that had to be told both
    /// could be told two different things.
    /// </remarks>
    public static string EnvironmentName => StackName[(StackName.IndexOf('-', StringComparison.Ordinal) + 1)..];

    /// <summary>The stack, as the workflow named it.</summary>
    public static string StackName { get; } =
        Environment.GetEnvironmentVariable(StackNameVariable) ?? string.Empty;

    public IAmazonSQS Sqs => _sqs.Value;

    public IAmazonDynamoDB DynamoDb => _dynamoDb.Value;

    public IAmazonCloudWatchLogs Logs => _logs.Value;

    public IAmazonCloudWatch Metrics => _metrics.Value;

    /// <summary>
    /// Polls until something is there, or the deadline passes.
    /// </summary>
    /// <typeparam name="T">What is being waited for.</typeparam>
    /// <param name="read">Reads it, returning null while it is not there yet.</param>
    /// <param name="within">How long to wait before giving up.</param>
    /// <param name="every">How long to wait between reads.</param>
    /// <returns>What was read, or null if the deadline passed first.</returns>
    /// <remarks>
    /// Every assertion here is about something that happens after a message is sent, through services
    /// that are eventually consistent about saying so. A read that ran once would fail on timing far
    /// more often than on behaviour, and a test that fails for timing is one people rerun rather than
    /// read.
    /// </remarks>
    public static async Task<T?> Until<T>(
        Func<Task<T?>> read,
        TimeSpan within,
        TimeSpan? every = null)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(read);

        var interval = every ?? TimeSpan.FromSeconds(3);
        var deadline = DateTimeOffset.UtcNow + within;

        while (true)
        {
            if (await read().ConfigureAwait(false) is { } found)
            {
                return found;
            }

            if (DateTimeOffset.UtcNow + interval > deadline)
            {
                return null;
            }

            await Task.Delay(interval).ConfigureAwait(false);
        }
    }

    /// <summary>Closes the clients that were opened, and only those.</summary>
    public void Dispose()
    {
        foreach (var client in new IDisposable?[]
                 {
                     _sqs.IsValueCreated ? _sqs.Value : null,
                     _dynamoDb.IsValueCreated ? _dynamoDb.Value : null,
                     _logs.IsValueCreated ? _logs.Value : null,
                     _metrics.IsValueCreated ? _metrics.Value : null,
                 })
        {
            client?.Dispose();
        }
    }

    /// <summary>
    /// The outputs of the named stack, read from the file the deployment wrote.
    /// </summary>
    private static Dictionary<string, string> Outputs()
    {
        var path = Environment.GetEnvironmentVariable(OutputsFileVariable);

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new InvalidOperationException(
                $"{OutputsFileVariable} names '{path}', which is not a file. {SkipReason}");
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));

        return !document.RootElement.TryGetProperty(StackName, out JsonElement stack)
            ? throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                $"'{path}' holds no outputs for stack '{StackName}'. It carries: " +
                $"{string.Join(", ", document.RootElement.EnumerateObject().Select(entry => entry.Name))}."))
            : stack.EnumerateObject().ToDictionary(entry => entry.Name,
                entry => entry.Value.GetString() ?? string.Empty, StringComparer.Ordinal);
    }

    private string Required(string name) =>
        _outputs.Value.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"The deployment published no {name}, so these tests cannot find what to assert on.");

    private readonly Lazy<Dictionary<string, string>> _outputs;
    private readonly Lazy<IAmazonSQS> _sqs;
    private readonly Lazy<IAmazonDynamoDB> _dynamoDb;
    private readonly Lazy<IAmazonCloudWatchLogs> _logs;
    private readonly Lazy<IAmazonCloudWatch> _metrics;

    private static bool Configured() =>
        !string.IsNullOrWhiteSpace(StackName)
        && Environment.GetEnvironmentVariable(OutputsFileVariable) is { Length: > 0 } path
        && File.Exists(path);
}
