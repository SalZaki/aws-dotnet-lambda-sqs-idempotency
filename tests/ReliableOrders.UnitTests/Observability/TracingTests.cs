using System.Diagnostics;
using ReliableOrders.Aws.Sqs;
using ReliableOrders.Core.Observability;
using ReliableOrders.Core.Persistence;
using ReliableOrders.Core.Processing;
using ReliableOrders.UnitTests.Processing;
using ReliableOrders.UnitTests.Validation;

namespace ReliableOrders.UnitTests.Observability;

/// <summary>
/// What a record's trace contains, what links it to the publisher's, and what it must never carry.
/// </summary>
/// <remarks>
/// Asserted against the real <see cref="ActivitySource"/> through a listener, so these observe the
/// spans a collector would rather than a stand-in's record of them. Every test identifies its own
/// spans by message identifier or trace identifier, because a listener is process-wide and other test
/// classes are running beside it.
/// </remarks>
public sealed class TracingTests
{
    /// <summary>
    /// A record produces one span, carrying what an operator needs to find it.
    /// </summary>
    /// <remarks>
    /// The messaging attributes are the OpenTelemetry convention, so a backend renders this as the
    /// consuming half of a producer-consumer pair rather than as an unexplained internal step. The
    /// invocation identifier is what ties one batch's records together in the absence of a batch span,
    /// which is deliberately not created — see <see cref="RecordTrace"/>.
    /// </remarks>
    [Fact]
    public async Task A_record_span_carries_the_message_and_the_invocation()
    {
        using var capture = new SpanCapture();
        using var harness = new BatchHarness();
        var messageId = NewMessageId();

        await harness.HandleAsync([messageId]);

        var span = capture.RecordFor(messageId);

        Assert.Equal(ActivityKind.Consumer, span.Kind);
        Assert.Equal(Tracing.MessagingSystemValue, span.GetTagItem(Tracing.Attributes.MessagingSystem));
        Assert.Equal(Tracing.MessagingOperationValue, span.GetTagItem(Tracing.Attributes.MessagingOperation));
        Assert.Equal(messageId, span.GetTagItem(Tracing.Attributes.MessagingMessageId));
        Assert.Equal(1, span.GetTagItem(Tracing.Attributes.ReceiveCount));
        Assert.Equal(BatchHarness.LambdaRequestId, span.GetTagItem(Tracing.Attributes.FaasInvocationId));
    }

    /// <summary>
    /// A publisher's trace context makes the record part of the publisher's trace.
    /// </summary>
    /// <remarks>
    /// The point of the whole propagation path, and the one thing about it worth stating twice: SQS
    /// links nothing on its own, and the event source mapping links nothing either. This trace is
    /// continuous only because the publisher wrote a header and the mapper carried it through.
    /// </remarks>
    [Fact]
    public async Task A_publishers_trace_context_becomes_the_records_parent()
    {
        using var capture = new SpanCapture();
        using var harness = new BatchHarness();
        var messageId = NewMessageId();

        var published = new ActivityContext(
            ActivityTraceId.CreateRandom(),
            ActivitySpanId.CreateRandom(),
            ActivityTraceFlags.Recorded);

        harness.TraceParents[messageId] = TraceParent(published);

        await harness.HandleAsync([messageId]);

        var span = capture.RecordFor(messageId);

        Assert.Equal(published.TraceId, span.TraceId);
        Assert.Equal(published.SpanId, span.ParentSpanId);
    }

    /// <summary>
    /// An extracted parent is marked remote.
    /// </summary>
    /// <remarks>
    /// The three-argument <c>ActivityContext.TryParse</c> leaves the flag false, which would say the
    /// parent span ran in this process. Consumers act on it: the X-Ray translator reads parent
    /// remoteness to decide whether this span is the service's entry point or a subsegment of
    /// something that never executed here, so the flag decides how the trace reads rather than
    /// decorating it.
    /// </remarks>
    [Fact]
    public async Task An_extracted_parent_is_marked_remote()
    {
        using var capture = new SpanCapture();
        using var harness = new BatchHarness();
        var messageId = NewMessageId();

        harness.TraceParents[messageId] = TraceParent(new ActivityContext(
            ActivityTraceId.CreateRandom(),
            ActivitySpanId.CreateRandom(),
            ActivityTraceFlags.Recorded));

        await harness.HandleAsync([messageId]);

        Assert.True(capture.RecordFor(messageId).HasRemoteParent);
    }

    /// <summary>
    /// A record whose processing throws is marked errored, with the outcome the log and metric use.
    /// </summary>
    /// <remarks>
    /// The outcome is normally written after processing returns, and this is the path where it does
    /// not return. Without it the record that failed for an unexplained reason — the one an operator
    /// most wants — is the only one missing from a search for errored spans, while the log line and
    /// the metric both call it a failure.
    /// </remarks>
    [Fact]
    public async Task A_record_whose_processing_throws_is_marked_errored()
    {
        using var capture = new SpanCapture();
        using var harness = new BatchHarness();
        var messageId = NewMessageId();

        harness.Throwing.Add(messageId);

        await harness.HandleAsync([messageId]);

        var span = capture.RecordFor(messageId);

        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Equal(
            nameof(MessageProcessingOutcome.TransientFailure),
            span.GetTagItem(Tracing.Attributes.Outcome));
    }

    /// <summary>
    /// A message with no trace context still produces a span, as a root.
    /// </summary>
    /// <remarks>
    /// A publisher that does not propagate is the ordinary case, not a fault. Losing the record's span
    /// because of it would remove exactly the orders whose publishers are least instrumented.
    /// </remarks>
    [Fact]
    public async Task A_message_with_no_trace_context_produces_a_root_span()
    {
        using var capture = new SpanCapture();
        using var harness = new BatchHarness();
        var messageId = NewMessageId();

        await harness.HandleAsync([messageId]);

        Assert.Equal(default, capture.RecordFor(messageId).ParentSpanId);
    }

    /// <summary>
    /// A malformed <c>traceparent</c> is ignored rather than failing the record.
    /// </summary>
    /// <remarks>
    /// Tracing is diagnostic. A publisher writing a broken header still published an order, and
    /// refusing it over a telemetry field would turn a monitoring defect into a dead-lettered message.
    /// </remarks>
    [Theory]
    [InlineData("not-a-traceparent")]
    [InlineData("00-tooshort-0000000000000001-01")]
    [InlineData("")]
    public async Task A_malformed_trace_context_is_ignored(string traceParent)
    {
        using var capture = new SpanCapture();
        using var harness = new BatchHarness();
        var messageId = NewMessageId();

        harness.TraceParents[messageId] = traceParent;

        var response = await harness.HandleAsync([messageId]);

        Assert.Empty(response.BatchItemFailures);
        Assert.Equal(default, capture.RecordFor(messageId).ParentSpanId);
    }

    /// <summary>
    /// A record deferred at the deadline produces no span at all.
    /// </summary>
    /// <remarks>
    /// It was never attempted. A span would put a zero-length step into the publisher's trace for work
    /// this invocation declined to start, which reads as a suspiciously fast success. The deferral is
    /// reported as a log event and a metric instead.
    /// </remarks>
    [Fact]
    public async Task A_deferred_record_produces_no_span()
    {
        using var capture = new SpanCapture();
        using var harness = new BatchHarness();
        var messageId = NewMessageId();

        harness.Deadline = BatchHarness.Now - TimeSpan.FromSeconds(1);

        await harness.HandleAsync([messageId]);

        Assert.False(capture.HasRecordFor(messageId));
    }

    /// <summary>
    /// Processing a message produces a span for each step it ran.
    /// </summary>
    /// <remarks>
    /// The five the specification asks for, minus the two the store owns — this harness stands in for
    /// the store, so persistence and classification are covered where the store is. What matters here
    /// is that each step is its own span rather than one opaque interval, which is what makes a
    /// latency change attributable to canonical hashing rather than to "processing".
    /// </remarks>
    [Fact]
    public async Task Each_processing_step_is_its_own_span()
    {
        using var capture = new SpanCapture();
        using var harness = new ProcessorHarness();
        using var record = Tracing.Source.StartActivity(Tracing.Spans.ProcessRecord);

        Assert.NotNull(record);

        await harness.ProcessAsync(ProcessorHarness.ValidBody(), TestContext.Current.CancellationToken);

        var names = capture.NamesInTrace(record.TraceId);

        Assert.Contains(Tracing.Spans.Parse, names);
        Assert.Contains(Tracing.Spans.Validate, names);
        Assert.Contains(Tracing.Spans.Hash, names);
    }

    /// <summary>
    /// A body that never parses produces the parse span and no step beyond it.
    /// </summary>
    /// <remarks>
    /// Reading a trace should show where a record stopped. Spans for steps that never ran would say
    /// the event was validated and hashed when nothing of the sort happened.
    /// </remarks>
    [Fact]
    public async Task A_body_that_will_not_parse_produces_no_step_beyond_parsing()
    {
        using var capture = new SpanCapture();
        using var harness = new ProcessorHarness();
        using var record = Tracing.Source.StartActivity(Tracing.Spans.ProcessRecord);

        Assert.NotNull(record);

        await harness.ProcessAsync("{ not json", TestContext.Current.CancellationToken);

        var names = capture.NamesInTrace(record.TraceId);

        Assert.Contains(Tracing.Spans.Parse, names);
        Assert.DoesNotContain(Tracing.Spans.Validate, names);
        Assert.DoesNotContain(Tracing.Spans.Hash, names);
    }

    /// <summary>
    /// The record's span records the outcome, in the vocabulary the logs use.
    /// </summary>
    /// <remarks>
    /// One vocabulary across logs, metrics and traces. An operator moving between the three should not
    /// have to translate, and a trace saying <c>Duplicate</c> where a log says something else would be
    /// two accounts of one record.
    /// </remarks>
    [Fact]
    public async Task The_record_span_records_the_outcome()
    {
        using var capture = new SpanCapture();
        using var harness = new ProcessorHarness();
        using var record = Tracing.Source.StartActivity(Tracing.Spans.ProcessRecord);

        Assert.NotNull(record);

        await harness.ProcessAsync(ProcessorHarness.ValidBody(), TestContext.Current.CancellationToken);

        Assert.Equal(
            nameof(MessageProcessingOutcome.Processed),
            record.GetTagItem(Tracing.Attributes.Outcome));
        Assert.Equal(ActivityStatusCode.Ok, record.Status);
    }

    /// <summary>
    /// A returned record marks its span as errored, and a duplicate does not.
    /// </summary>
    /// <remarks>
    /// The status follows whether the record is coming back, not whether it succeeded. A duplicate is
    /// the idempotency mechanism working: marking it an error would fill an error view with the
    /// outcome this service exists to produce.
    /// </remarks>
    [Fact]
    public async Task A_duplicate_is_not_an_error_and_an_invalid_event_is()
    {
        using var capture = new SpanCapture();

        var duplicate = await SpanOf(
            ProcessorHarness.ValidBody(),
            new OrderWriteResult.Duplicate(DuplicateScope.Event));

        Assert.Equal(ActivityStatusCode.Ok, duplicate.Status);
        Assert.Equal(
            nameof(MessageProcessingOutcome.Duplicate),
            duplicate.GetTagItem(Tracing.Attributes.Outcome));

        Assert.Equal(ActivityStatusCode.Error, (await SpanOf("{ not json")).Status);
    }

    /// <summary>
    /// The identifiers on a record span are the three the log scope carries, and nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The counterpart to <c>LogRedactionTests</c>, and there for the same reason. Traces are sampled
    /// into a different system under different retention, so the Do Not Log list does not stop
    /// applying because the destination changed. This pins the attribute set rather than checking for
    /// known-bad values, because a list of what must not appear only catches what someone thought of.
    /// </para>
    /// <para>
    /// The set it pins is what the processor writes, which is where the parsed event is in reach. The
    /// transport's own attributes are pinned by the case below, and they have to be pinned separately:
    /// the record span is started by the transport, and no harness runs the real handler and the real
    /// processor together.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_record_span_carries_no_customer_data()
    {
        using var capture = new SpanCapture();
        using var harness = new ProcessorHarness();
        using var record = Tracing.Source.StartActivity(Tracing.Spans.ProcessRecord);

        Assert.NotNull(record);

        var orderEvent = ValidEvent.Create();

        await harness.ProcessAsync(
            ProcessorHarness.Serialize(orderEvent),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            [
                Tracing.Attributes.CorrelationId,
                Tracing.Attributes.EventId,
                Tracing.Attributes.OrderId,
                Tracing.Attributes.Outcome,
            ],
            record.TagObjects.Select(tag => tag.Key).Order(StringComparer.Ordinal));

        var written = string.Join('', record.TagObjects.Select(tag => tag.Value?.ToString()));

        Assert.DoesNotContain(orderEvent.Data.CustomerId, written, StringComparison.Ordinal);
        Assert.DoesNotContain(orderEvent.Data.ItemDescription, written, StringComparison.Ordinal);
        Assert.DoesNotContain(
            orderEvent.Data.AmountMinor.ToString(System.Globalization.CultureInfo.InvariantCulture),
            written,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The transport writes the five messaging attributes and nothing beyond them.
    /// </summary>
    /// <remarks>
    /// The other half of the redaction guarantee, and the half with the most within reach: this is the
    /// layer holding the raw SQS record, so a body, a receipt handle and every message attribute the
    /// publisher sent are all one line away. Run through the batch handler, because that is what starts
    /// a record span in production — the case above starts one itself and would see none of this.
    /// </remarks>
    [Fact]
    public async Task The_transport_writes_no_span_attribute_beyond_the_messaging_set()
    {
        using var capture = new SpanCapture();
        using var harness = new BatchHarness();
        var messageId = NewMessageId();

        await harness.HandleAsync([messageId]);

        Assert.Equal(
            [
                Tracing.Attributes.FaasInvocationId,
                Tracing.Attributes.MessagingMessageId,
                Tracing.Attributes.MessagingOperation,
                Tracing.Attributes.MessagingSystem,
                Tracing.Attributes.ReceiveCount,
            ],
            capture.RecordFor(messageId).TagObjects.Select(tag => tag.Key).Order(StringComparer.Ordinal));
    }

    /// <remarks>
    /// Fresh per test, because a listener is process-wide and every assertion here finds its spans by
    /// this value.
    /// </remarks>
    private static string NewMessageId() => Guid.NewGuid().ToString();

    private static string TraceParent(ActivityContext context) =>
        $"00-{context.TraceId}-{context.SpanId}-01";

    /// <summary>
    /// The record span one body left behind, with the store returning what the caller asked for.
    /// </summary>
    /// <remarks>
    /// Returned rather than reduced to a status, so a caller can assert the outcome attribute beside it.
    /// The two have to agree — a span marked <c>Ok</c> is only correct if the outcome it carries is one
    /// that belongs there — and a helper that answered only the status would let a test claim to be
    /// about duplicates while exercising something else.
    /// </remarks>
    private static async Task<Activity> SpanOf(string body, OrderWriteResult? stored = null)
    {
        using var harness = new ProcessorHarness();

        if (stored is not null)
        {
            harness.StoreResult = stored;
        }

        using var record = Tracing.Source.StartActivity(Tracing.Spans.ProcessRecord);

        Assert.NotNull(record);

        await harness.ProcessAsync(body, TestContext.Current.CancellationToken);

        return record;
    }
}
