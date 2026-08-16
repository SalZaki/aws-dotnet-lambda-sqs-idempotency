using System.Text.Json;
using Amazon.Lambda.SQSEvents;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using ReliableOrders.Aws.Sqs;
using ReliableOrders.Core.Observability;
using ReliableOrders.Core.Persistence;
using ReliableOrders.Core.Processing;
using ReliableOrders.UnitTests.Observability;

namespace ReliableOrders.UnitTests.Processing;

/// <summary>
/// Runs a batch through the real handler, log and metrics publisher.
/// </summary>
/// <remarks>
/// The processor is a stand-in here, unlike in <see cref="ProcessorHarness"/>. These tests are about
/// what the handler does with results — which records it returns, which it does not, and what it
/// reports — so a processor whose outcome each test chooses per message is the point rather than a
/// compromise. Its behaviour when it processes a message is already pinned one layer down.
/// </remarks>
internal sealed class BatchHarness : IDisposable
{
    private readonly JsonLogCapture _logs = new();
    private readonly EmbeddedMetricsCapture _metrics = new();
    private readonly ILoggerFactory _factory;
    private readonly StubProcessor _processor = new();
    private readonly CancellationTokenSource _cancellation = new();

    public BatchHarness()
    {
        _factory = JsonLogCapture.FactoryFor(_logs);
        _processor.Harness = this;

        Handler = new SqsBatchHandler(
            _processor,
            _metrics.Publisher,
            new ProcessingLog(_factory.CreateLogger<ProcessingLog>(), "reliable-orders", "test"),
            new FakeTimeProvider(Now));
    }

    public static readonly DateTimeOffset Now = new(2026, 8, 9, 12, 0, 0, TimeSpan.Zero);

    public const string LambdaRequestId = "8f0a5d5e-1f2b-4c6d-9f5a-2b7c8d9e0f11";

    public SqsBatchHandler Handler { get; }

    /// <summary>
    /// The deadline for the next batch. Comfortably ahead unless a test moves it.
    /// </summary>
    public DateTimeOffset Deadline { get; set; } = Now + TimeSpan.FromMinutes(1);

    /// <summary>
    /// What the store would have returned, per message identifier. Created unless told otherwise.
    /// </summary>
    public Dictionary<string, OrderWriteResult> Outcomes { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// A body to use instead of a valid one, per message identifier.
    /// </summary>
    public Dictionary<string, string> Bodies { get; } = new(StringComparer.Ordinal);

    /// <summary>Message identifiers whose processing throws, standing in for a defect.</summary>
    public HashSet<string> Throwing { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// A W3C <c>traceparent</c> to put on a message, per message identifier.
    /// </summary>
    /// <remarks>
    /// Written as a message attribute, which is where a publisher writes it and the only place SQS
    /// carries it. A message absent from here has none, which is the ordinary case for a publisher
    /// that does not propagate.
    /// </remarks>
    public Dictionary<string, string> TraceParents { get; } = new(StringComparer.Ordinal);

    /// <summary>Message identifiers whose processing reports the invocation is ending.</summary>
    public HashSet<string> Cancelling { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Message identifiers whose processing throws a client-side timeout.
    /// </summary>
    /// <remarks>
    /// A <see cref="TaskCanceledException"/> with nothing cancelled, which is how an AWS SDK HTTP
    /// timeout arrives. It derives from <see cref="OperationCanceledException"/>, so a handler that
    /// rethrows that type unconditionally would lose the whole batch to one slow socket.
    /// </remarks>
    public HashSet<string> TimingOut { get; } = new(StringComparer.Ordinal);

    /// <summary>How many messages actually reached the processor.</summary>
    public int ProcessedCount => _processor.Processed;

    public IReadOnlyList<JsonElement> LogLines => _logs.Lines;

    public JsonElement MetricRecord => _metrics.SingleRecord;

    /// <summary>
    /// The line the invocation ended on, which is the only one that admits a partial failure.
    /// </summary>
    public JsonElement BatchCompletedLine =>
        _logs.Lines.Single(line =>
            line.GetProperty("LogEvent").GetString() == nameof(ProcessingLog.BatchCompleted));

    /// <remarks>
    /// Runs on the harness's own token, linked to the test's, so a stand-in can cancel it the way the
    /// runtime would when an invocation is ending. Genuine cancellation is the token being cancelled,
    /// not merely the exception type being thrown.
    /// </remarks>
    public async Task<SQSBatchResponse> HandleAsync(IEnumerable<string> messageIds)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            _cancellation.Token,
            TestContext.Current.CancellationToken);

        return await Handler.HandleAsync(
            new SQSEvent { Records = [.. messageIds.Select(Record)] },
            new BatchInvocation(LambdaRequestId, Deadline),
            linked.Token);
    }

    /// <summary>Cancels the harness's token, standing in for an invocation that is ending.</summary>
    internal void Cancel() => _cancellation.Cancel();

    public static string[] Identifiers(SQSBatchResponse response) =>
        [.. response.BatchItemFailures.Select(failure => failure.ItemIdentifier)];

    public void Dispose()
    {
        _cancellation.Dispose();
        _factory.Dispose();
        _logs.Dispose();
        _metrics.Dispose();
    }

    private SQSEvent.SQSMessage Record(string messageId) => new()
    {
        MessageId = messageId,
        Body = Bodies.TryGetValue(messageId, out var body) ? body : ProcessorHarness.ValidBody(),
        Attributes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [IncomingMessageMapper.ApproximateReceiveCountAttribute] = "1",
        },
        MessageAttributes = TraceParents.TryGetValue(messageId, out var traceParent)
            ? new Dictionary<string, SQSEvent.MessageAttribute>(StringComparer.Ordinal)
            {
                [RecordTrace.TraceParentAttribute] = new()
                {
                    DataType = "String",
                    StringValue = traceParent,
                },
            }
            : [],
    };

    /// <remarks>
    /// Returns the outcome the test asked for, and counts what reached it so a test can assert that a
    /// deferred record was never attempted.
    /// </remarks>
    private sealed class StubProcessor : IOrderMessageProcessor
    {
        public int Processed { get; private set; }

        public Task<MessageProcessingResult> ProcessAsync(
            IncomingMessage message,
            IInvocationMetrics metrics,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(message);

            Processed++;

            if (Harness.Cancelling.Contains(message.MessageId))
            {
                // Cancelled first, then thrown. The handler rethrows only when this token is actually
                // cancelled, which is what separates an invocation ending from a socket timing out.
                Harness.Cancel();

                throw new OperationCanceledException(cancellationToken);
            }

            if (Harness.TimingOut.Contains(message.MessageId))
            {
                throw new TaskCanceledException("client-side HTTP timeout, nothing cancelled");
            }

            if (Harness.Throwing.Contains(message.MessageId))
            {
                throw new InvalidOperationException("a defect below the handler");
            }

            if (Harness.Bodies.ContainsKey(message.MessageId))
            {
                return Task.FromResult(
                    Record(message, metrics, MessageProcessingOutcome.PermanentFailure));
            }

            var outcome = Harness.Outcomes.TryGetValue(message.MessageId, out var written)
                ? written
                : new OrderWriteResult.Created();

            return Task.FromResult(Record(
                message,
                metrics,
                outcome.Match(
                    whenCreated: _ => MessageProcessingOutcome.Processed,
                    whenDuplicate: _ => MessageProcessingOutcome.Duplicate,
                    whenConflict: _ => MessageProcessingOutcome.PermanentFailure,
                    whenTransientFault: _ => MessageProcessingOutcome.TransientFailure,
                    whenPermanentFault: _ => MessageProcessingOutcome.PermanentFailure)));
        }

        /// <summary>Set once by the harness that owns it, before any batch runs.</summary>
        public BatchHarness Harness { get; set; } = null!;

        /// <remarks>
        /// Records the metric its outcome would record. The real processor writes the log event, the
        /// metric and the result together, and a stand-in that returned a result while recording
        /// nothing would leave batch-level metric assertions passing against a publisher no message
        /// ever reached.
        /// </remarks>
        private static MessageProcessingResult Record(
            IncomingMessage message,
            IInvocationMetrics metrics,
            MessageProcessingOutcome outcome)
        {
            switch (outcome)
            {
                case MessageProcessingOutcome.Processed:
                    metrics.OrderProcessed(TimeSpan.Zero);
                    break;
                case MessageProcessingOutcome.Duplicate:
                    metrics.DuplicateEvent(TimeSpan.Zero);
                    break;
                case MessageProcessingOutcome.PermanentFailure:
                    metrics.InvalidEvent(message.ApproximateReceiveCount, TimeSpan.Zero);
                    break;
                case MessageProcessingOutcome.TransientFailure:
                    metrics.TransientFailure(TimeSpan.Zero);
                    break;
                default:
                    Assert.Fail($"{outcome} has no case here. Add one alongside the outcome.");
                    break;
            }

            return new MessageProcessingResult(message.MessageId, outcome, Reason: null, TimeSpan.Zero);
        }
    }
}
