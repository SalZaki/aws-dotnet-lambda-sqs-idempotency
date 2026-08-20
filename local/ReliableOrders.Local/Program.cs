using System.Runtime.InteropServices;
using Amazon.DynamoDBv2;
using Amazon.Runtime;
using Amazon.SQS;
using ReliableOrders.Local;

// The local development stack's own program. Two verbs, one image: `provision` creates the queues,
// the redrive policy and the two tables and exits, and `run` stands in for the event source mapping
// until it is stopped. Compose runs the first to completion before it starts the second.
//
// Nothing here is deployed. See the Local Development Stack section of docs/testing-strategy.md for
// what the stack is authoritative for and what it is not.

using var stopping = new CancellationTokenSource();

// SIGTERM as well as SIGINT. `docker compose down` sends the first and a terminal sends the second,
// and a container that ignored SIGTERM would be killed ten seconds later — long enough for a
// developer to conclude the stack hangs on shutdown.
using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, Stop);
using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, Stop);

void Stop(PosixSignalContext context)
{
    ArgumentNullException.ThrowIfNull(context);

    // Handled here rather than left to the runtime's own shutdown, so the loop finishes the batch it
    // is applying instead of being torn down between a delete and the next one.
    context.Cancel = true;

    Log.Line("Stopping.");

    stopping.Cancel();
}

try
{
    var verb = args.Length == 1
        ? args[0]
        : throw new InvalidOperationException(
            $"Expected exactly one verb, `{Verbs.Provision}` or `{Verbs.Run}`, and was given "
            + $"{args.Length} arguments.");

    var configuration = LocalConfiguration.FromEnvironment();

    // Credentials are required by the SDK and ignored by both emulators. They are obviously fake so
    // that nobody reads this as a place real ones could be needed: no part of this stack touches an
    // AWS account, and the endpoints above are required for the same reason.
    var credentials = new BasicAWSCredentials("local", "local");

    using var sqs = new AmazonSQSClient(
        credentials,
        new AmazonSQSConfig
        {
            ServiceURL = configuration.SqsEndpoint.ToString(),
            AuthenticationRegion = configuration.Region,
        });

    switch (verb)
    {
        case Verbs.Provision:
            await ProvisionAsync(configuration, credentials, sqs, stopping.Token);

            break;

        case Verbs.Run:
            await RunAsync(configuration, sqs, stopping.Token);

            break;

        default:
            throw new InvalidOperationException(
                $"`{verb}` is not a verb this program has. Use `{Verbs.Provision}` or `{Verbs.Run}`.");
    }

    return 0;
}
catch (OperationCanceledException) when (stopping.IsCancellationRequested)
{
    // Stopped on purpose. Nothing failed, so nothing is reported and the exit code says so — a
    // non-zero exit here would make every `docker compose down` look like a crash.
    //
    // Guarded on the token rather than on the type. A timed-out HTTP call throws the same exception
    // and means the opposite, and an unguarded catch here reported a wedged function as a clean
    // shutdown: exit zero, no message, and a stack left half-up. See Stopping.
    return 0;
}
catch (Exception failure)
{
    await Console.Error.WriteLineAsync(failure.ToString());

    return 1;
}

static async Task ProvisionAsync(
    LocalConfiguration configuration,
    AWSCredentials credentials,
    IAmazonSQS sqs,
    CancellationToken cancellationToken)
{
    using var dynamoDb = new AmazonDynamoDBClient(
        credentials,
        new AmazonDynamoDBConfig
        {
            ServiceURL = configuration.DynamoDbEndpoint.ToString(),
            AuthenticationRegion = configuration.Region,
        });

    // Each emulator's work is done as soon as that emulator is ready, rather than waiting for both.
    // The two are independent, and a stack whose DynamoDB was up would otherwise report nothing at
    // all while LocalStack activated its licence.
    await Emulators.WaitForAsync(
        "DynamoDB",
        configuration.DynamoDbEndpoint,
        token => dynamoDb.ListTablesAsync(1, token),
        cancellationToken);

    await LocalTables.CreateAsync(
        dynamoDb,
        configuration.OrdersTableName,
        configuration.IdempotencyTableName,
        cancellationToken);

    await Emulators.WaitForAsync(
        "SQS",
        configuration.SqsEndpoint,
        token => sqs.ListQueuesAsync(string.Empty, token),
        cancellationToken);

    var queues = await LocalQueues.CreateAsync(sqs, configuration.EnvironmentName, cancellationToken);

    Log.Line($"Orders table       {configuration.OrdersTableName}");
    Log.Line($"Idempotency table  {configuration.IdempotencyTableName}");
    Log.Line($"Source queue       {queues.SourceQueueUrl}");
    Log.Line($"Dead-letter queue  {queues.DeadLetterQueueUrl}");
}

static async Task RunAsync(
    LocalConfiguration configuration,
    IAmazonSQS sqs,
    CancellationToken cancellationToken)
{
    // A backstop rather than the working bound. The emulator enforces the function's own timeout and
    // answers "Task timed out" when it elapses, so a wedged handler is already reported as one; what
    // this covers is the emulator itself not answering. Comfortably above that timeout, so the two
    // cannot race and report the slower failure of the pair.
    using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(1) };

    var queues = await LocalQueues.ResolveAsync(sqs, configuration.EnvironmentName, cancellationToken);

    var mapping = await EventSourceMapping.ForAsync(
        sqs,
        new FunctionInvoker(http, configuration.FunctionUrl),
        queues,
        configuration,
        cancellationToken);

    await mapping.RunAsync(cancellationToken);
}

/// <summary>
/// What this program can be asked to do.
/// </summary>
internal static class Verbs
{
    /// <summary>Create the queues, the redrive policy and the two tables, then exit.</summary>
    internal const string Provision = "provision";

    /// <summary>Poll the queue and invoke the function until stopped.</summary>
    internal const string Run = "run";
}
