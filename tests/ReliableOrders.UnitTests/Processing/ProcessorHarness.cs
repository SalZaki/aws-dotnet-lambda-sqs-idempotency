using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using ReliableOrders.Core.Contracts;
using ReliableOrders.Core.Idempotency;
using ReliableOrders.Core.Observability;
using ReliableOrders.Core.Persistence;
using ReliableOrders.Core.Processing;
using ReliableOrders.Core.Validation;
using ReliableOrders.UnitTests.Observability;
using ReliableOrders.UnitTests.Validation;

namespace ReliableOrders.UnitTests.Processing;

/// <summary>
/// Runs one message through a processor built from the real parser, validator and hasher.
/// </summary>
/// <remarks>
/// <para>
/// Only the store is a stand-in, because it is the one collaborator whose outcome a test needs to
/// choose. Everything before it stays real, so a test that feeds a malformed body exercises the
/// parser that will see it in production rather than a fake agreeing with the test's assumption.
/// </para>
/// <para>
/// The log and the metrics are real too, writing into the same captures the observability suites
/// use. That is what lets one assertion cover the thing this story is actually about: the result, the
/// log event and the metric all naming the same outcome. Three fakes recording three calls would
/// prove they were called, not that they agree.
/// </para>
/// </remarks>
internal sealed class ProcessorHarness : IDisposable
{
    private readonly JsonLogCapture _logs = new();
    private readonly EmbeddedMetricsCapture _metrics = new();
    private readonly ILoggerFactory _factory;
    private readonly StubOrderCommandStore _store = new();

    public ProcessorHarness()
    {
        _factory = JsonLogCapture.FactoryFor(_logs);

        Processor = new OrderMessageProcessor(
            new OrderEventParser(),
            new OrderEventValidator(new FakeTimeProvider(ValidEvent.Now), EventSkewWindow.Default),
            new CanonicalPayloadHasher(),
            _store,
            new ProcessingLog(_factory.CreateLogger<ProcessingLog>(), Service, Environment),
            TimeProvider.System);
    }

    public const string Service = "reliable-orders";
    public const string Environment = "test";
    public const string MessageId = "3a1c9a02-6f28-4a1a-9d3b-1f9f6c2b7e44";

    public IOrderMessageProcessor Processor { get; }

    /// <summary>
    /// What the store will return for the next message.
    /// </summary>
    public OrderWriteResult StoreResult
    {
        get => _store.Result;
        set => _store.Result = value;
    }

    /// <summary>
    /// The cancellation token the store was handed, so a test can assert it was forwarded.
    /// </summary>
    public CancellationToken StoreToken => _store.Token;

    /// <summary>
    /// The single log line the message produced.
    /// </summary>
    /// <remarks>
    /// Single by design: a processed message writes exactly one terminal event, and a second line
    /// would mean an outcome was reported twice.
    /// </remarks>
    public JsonElement LogLine => _logs.SingleLine;

    /// <summary>
    /// The metric record for the invocation, available once <see cref="ProcessAsync"/> has returned.
    /// </summary>
    public JsonElement MetricRecord => _metrics.SingleRecord;

    /// <summary>
    /// Processes one body as a first delivery unless told otherwise.
    /// </summary>
    /// <remarks>
    /// The metrics accumulator is opened and disposed around the call, because a record is published
    /// when the invocation ends and a test asserting on it has to let that happen.
    /// </remarks>
    public async Task<MessageProcessingResult> ProcessAsync(
        string body,
        CancellationToken cancellationToken,
        int approximateReceiveCount = 1)
    {
        using var invocation = _metrics.Publisher.BeginInvocation(1);

        return await Processor.ProcessAsync(
            new IncomingMessage(MessageId, body, approximateReceiveCount, new Dictionary<string, string>()),
            invocation,
            cancellationToken);
    }

    /// <summary>
    /// A body that parses and validates, so a test can choose what the store does with it.
    /// </summary>
    public static string ValidBody() => Serialize(ValidEvent.Create());

    public static string Serialize(OrderCreatedV1 orderEvent) =>
        JsonSerializer.Serialize(orderEvent, OrderContractSerializerContext.Default.OrderCreatedV1);

    public void Dispose()
    {
        _factory.Dispose();
        _logs.Dispose();
        _metrics.Dispose();
    }

    /// <remarks>
    /// Returns whatever the test asked for, and records the token it was given. It never throws:
    /// the real store reports every outcome it can classify as a value, and a stand-in that threw
    /// would let a test pass against behaviour the interface forbids.
    /// </remarks>
    private sealed class StubOrderCommandStore : IOrderCommandStore
    {
        public OrderWriteResult Result { get; set; } = new OrderWriteResult.Created();

        public CancellationToken Token { get; private set; }

        public Task<OrderWriteResult> TryCreateAsync(
            OrderCreatedV1 message,
            PayloadHashes hashes,
            CancellationToken cancellationToken)
        {
            Token = cancellationToken;

            return Task.FromResult(Result);
        }
    }
}
