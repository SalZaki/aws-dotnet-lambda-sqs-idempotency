using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using ReliableOrders.Aws.Sqs;
using ReliableOrders.Core.Observability;
using ReliableOrders.Core.Processing;
using ReliableOrders.UnitTests.Observability;
using ReliableOrders.UnitTests.Processing;

namespace ReliableOrders.UnitTests.Composition;

/// <summary>
/// The entry point, and the one thing it computes.
/// </summary>
/// <remarks>
/// Turning the runtime's remaining time into a deadline is the only logic in <c>Function</c>, and it
/// is logic no other suite can reach: the batch tests construct a <c>BatchInvocation</c> with an
/// absolute instant precisely so they can place it where they need. So this is where a deadline
/// derived from the wrong clock would be caught, and until these existed it would not have been.
/// </remarks>
public sealed class FunctionTests
{
    /// <summary>
    /// The deadline comes from the injected clock, not from the machine's.
    /// </summary>
    /// <remarks>
    /// The fake clock is set decades in the past and the runtime reports no time remaining, so the
    /// deadline is behind the fake now and every record must be deferred untouched. Reading
    /// <see cref="TimeProvider.System"/> here instead — which is what this once did — puts the
    /// deadline decades ahead of the clock the handler compares it against, so nothing defers and the
    /// records are processed. The two readings disagree by design; that is what makes this test able
    /// to fail.
    /// </remarks>
    [Fact]
    public async Task The_deadline_is_derived_from_the_injected_clock()
    {
        using var harness = new EntryPointHarness();

        var response = await harness.HandleAsync(["m-1", "m-2"], remaining: TimeSpan.Zero);

        Assert.Equal(["m-1", "m-2"], BatchHarness.Identifiers(response));
        Assert.Equal(0, harness.ProcessedCount);
    }

    /// <summary>
    /// With time to spare, the batch is processed and nothing comes back.
    /// </summary>
    /// <remarks>
    /// The other side of the same comparison. Without it the test above would pass against an entry
    /// point that deferred unconditionally.
    /// </remarks>
    [Fact]
    public async Task A_batch_with_time_remaining_is_processed()
    {
        using var harness = new EntryPointHarness();

        var response = await harness.HandleAsync(["m-1", "m-2"], remaining: TimeSpan.FromMinutes(1));

        Assert.Empty(response.BatchItemFailures);
        Assert.Equal(2, harness.ProcessedCount);
    }

    /// <summary>
    /// The safety margin is held back rather than the whole of the remaining time being used.
    /// </summary>
    /// <remarks>
    /// Remaining time inside the margin leaves no room to finish a record, so the deadline is already
    /// past and everything defers. This is what fails if the entry point ever stops applying
    /// <see cref="ProcessingDeadline"/> and passes the raw remaining time.
    /// </remarks>
    [Fact]
    public async Task Remaining_time_inside_the_margin_defers_the_batch()
    {
        using var harness = new EntryPointHarness();

        var response = await harness.HandleAsync(
            ["m-1"],
            remaining: ProcessingDeadline.DefaultMargin - TimeSpan.FromMilliseconds(1));

        Assert.Equal(["m-1"], BatchHarness.Identifiers(response));
        Assert.Equal(0, harness.ProcessedCount);
    }

    /// <summary>
    /// The invocation's request identifier reaches every line the batch writes.
    /// </summary>
    [Fact]
    public async Task The_request_id_is_taken_from_the_lambda_context()
    {
        using var harness = new EntryPointHarness();

        await harness.HandleAsync(["m-1"], remaining: TimeSpan.FromMinutes(1));

        Assert.All(
            harness.LogLines,
            line => Assert.Equal(
                EntryPointHarness.RequestId,
                line.GetProperty(LogFields.LambdaRequestId).GetString()));
    }

    [Fact]
    public void A_function_needs_a_handler_and_a_clock()
    {
        using var harness = new EntryPointHarness();

        Assert.Throws<ArgumentNullException>(() => new ReliableOrders.Function.Function(null!, TimeProvider.System));
        Assert.Throws<ArgumentNullException>(() => new ReliableOrders.Function.Function(harness.Handler, null!));
    }

    [Fact]
    public async Task A_missing_lambda_context_is_a_caller_defect()
    {
        using var harness = new EntryPointHarness();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => harness.Function.HandleAsync(new SQSEvent { Records = [] }, context: null!));
    }

    /// <summary>
    /// An entry point over a real handler whose clock is the same fake the function reads.
    /// </summary>
    /// <remarks>
    /// Sharing the clock is the whole point: a function computing a deadline from one clock while the
    /// handler compares it against another is exactly the defect these tests exist to catch, and a
    /// harness that gave them separate clocks would reproduce it rather than detect it.
    /// </remarks>
    private sealed class EntryPointHarness : IDisposable
    {
        public const string RequestId = "8f0a5d5e-1f2b-4c6d-9f5a-2b7c8d9e0f11";

        /// <summary>Decades before any real clock, so the two cannot be confused.</summary>
        public static readonly DateTimeOffset Now = new(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);

        private readonly JsonLogCapture _logs = new();
        private readonly EmbeddedMetricsCapture _metrics = new();
        private readonly ILoggerFactory _factory;
        private readonly CountingProcessor _processor = new();

        public EntryPointHarness()
        {
            _factory = JsonLogCapture.FactoryFor(_logs);

            var clock = new FakeTimeProvider(Now);

            Handler = new SqsBatchHandler(
                _processor,
                _metrics.Publisher,
                new ProcessingLog(_factory.CreateLogger<ProcessingLog>(), "reliable-orders", "test"),
                clock);

            Function = new ReliableOrders.Function.Function(Handler, clock);
        }

        public SqsBatchHandler Handler { get; }

        public ReliableOrders.Function.Function Function { get; }

        public int ProcessedCount => _processor.Processed;

        public IReadOnlyList<System.Text.Json.JsonElement> LogLines => _logs.Lines;

        public Task<SQSBatchResponse> HandleAsync(IEnumerable<string> messageIds, TimeSpan remaining) =>
            Function.HandleAsync(
                new SQSEvent { Records = [.. messageIds.Select(Record)] },
                new StubLambdaContext(RequestId, remaining));

        public void Dispose()
        {
            _factory.Dispose();
            _logs.Dispose();
            _metrics.Dispose();
        }

        private static SQSEvent.SQSMessage Record(string messageId) => new()
        {
            MessageId = messageId,
            Body = ProcessorHarness.ValidBody(),
            Attributes = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [IncomingMessageMapper.ApproximateReceiveCountAttribute] = "1",
            },
        };

        /// <remarks>
        /// Counts what reached it, which is how a test asserts that a deferred record was never
        /// attempted. Every message succeeds, so a returned identifier can only be a deferral.
        /// </remarks>
        private sealed class CountingProcessor : IOrderMessageProcessor
        {
            public int Processed { get; private set; }

            public Task<MessageProcessingResult> ProcessAsync(
                IncomingMessage message,
                IInvocationMetrics metrics,
                CancellationToken cancellationToken)
            {
                ArgumentNullException.ThrowIfNull(message);
                ArgumentNullException.ThrowIfNull(metrics);

                Processed++;
                metrics.OrderProcessed(TimeSpan.Zero);

                return Task.FromResult(new MessageProcessingResult(
                    message.MessageId,
                    MessageProcessingOutcome.Processed,
                    Reason: null,
                    TimeSpan.Zero));
            }
        }

        /// <remarks>
        /// Only the two members the entry point reads are meaningful. The rest are what the interface
        /// requires and nothing consults.
        /// </remarks>
        private sealed class StubLambdaContext(string requestId, TimeSpan remaining) : ILambdaContext
        {
            public string AwsRequestId { get; } = requestId;

            public TimeSpan RemainingTime { get; } = remaining;

            public IClientContext ClientContext => null!;

            public string FunctionName => "reliable-orders";

            public string FunctionVersion => "$LATEST";

            public ICognitoIdentity Identity => null!;

            public string InvokedFunctionArn => string.Empty;

            public ILambdaLogger Logger => null!;

            public string LogGroupName => string.Empty;

            public string LogStreamName => string.Empty;

            public int MemoryLimitInMB => 512;
        }
    }
}
