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
/// Every outcome one message can reach, and whether the three things that report it agree.
/// </summary>
/// <remarks>
/// Each test asserts the result, the log event and the metric together. Splitting them would let the
/// three drift — a message counted as a duplicate, logged as created and returned as a failure would
/// pass three separate suites and be wrong in production.
/// </remarks>
public sealed class OrderMessageProcessorTests
{
    [Fact]
    public async Task A_new_order_is_processed()
    {
        using var harness = new ProcessorHarness();

        harness.StoreResult = new OrderWriteResult.Created();

        var result = await harness.ProcessAsync(ProcessorHarness.ValidBody(), TestContext.Current.CancellationToken);

        Assert.Equal(MessageProcessingOutcome.Processed, result.Outcome);
        Assert.False(result.ShouldReportAsFailure);
        Assert.Null(result.Reason);
        Assert.Equal(nameof(ProcessingLog.OrderCreated), LogEvent(harness));
        Assert.Equal(1, EmbeddedMetricsCapture.Count(harness.MetricRecord, MetricNames.OrdersProcessed));
    }

    /// <summary>
    /// A duplicate is a success, and is acknowledged like one.
    /// </summary>
    [Fact]
    public async Task A_duplicate_is_acknowledged_rather_than_returned()
    {
        using var harness = new ProcessorHarness();

        harness.StoreResult = new OrderWriteResult.Duplicate(DuplicateScope.Order);

        var result = await harness.ProcessAsync(ProcessorHarness.ValidBody(), TestContext.Current.CancellationToken);

        Assert.Equal(MessageProcessingOutcome.Duplicate, result.Outcome);
        Assert.False(result.ShouldReportAsFailure);
        Assert.Equal(nameof(ProcessingLog.DuplicateIgnored), LogEvent(harness));
        Assert.Equal(1, EmbeddedMetricsCapture.Count(harness.MetricRecord, MetricNames.DuplicateEvents));
        Assert.Equal(0, EmbeddedMetricsCapture.Count(harness.MetricRecord, MetricNames.BatchFailures));
    }

    /// <summary>
    /// A body that will not parse is permanent, and never reaches the store.
    /// </summary>
    [Theory]
    [InlineData("", ParseFailureReason.EmptyBody)]
    [InlineData("   ", ParseFailureReason.EmptyBody)]
    [InlineData("{ not json", ParseFailureReason.InvalidJson)]
    [InlineData("[]", ParseFailureReason.RootNotObject)]
    public async Task A_body_that_will_not_parse_is_a_permanent_failure(string body, string expectedReason)
    {
        using var harness = new ProcessorHarness();

        var result = await harness.ProcessAsync(body, TestContext.Current.CancellationToken);

        Assert.Equal(MessageProcessingOutcome.PermanentFailure, result.Outcome);
        Assert.True(result.ShouldReportAsFailure);
        Assert.Equal(expectedReason, result.Reason);
        Assert.Equal(nameof(ProcessingLog.MessageParsingFailed), LogEvent(harness));
        Assert.Equal(1, EmbeddedMetricsCapture.Count(harness.MetricRecord, MetricNames.ValidationFailures));
    }

    /// <summary>
    /// A schema version this build does not know is permanent, and says so distinctly.
    /// </summary>
    /// <remarks>
    /// Separate from a malformed body because the response differs: deploy a build that knows the
    /// version rather than go and fix the publisher.
    /// </remarks>
    [Fact]
    public async Task An_unsupported_schema_version_is_reported_as_its_own_reason()
    {
        using var harness = new ProcessorHarness();

        var body = ProcessorHarness.Serialize(
            ValidEvent.Create() with { SchemaVersion = OrderContract.SupportedSchemaVersion + 1 });

        var result = await harness.ProcessAsync(body, TestContext.Current.CancellationToken);

        Assert.Equal(MessageProcessingOutcome.PermanentFailure, result.Outcome);
        Assert.Equal(ParseFailureReason.UnsupportedSchemaVersion, result.Reason);
        Assert.Equal(nameof(ProcessingLog.MessageParsingFailed), LogEvent(harness));
    }

    /// <summary>
    /// An event that parses and breaks a rule is permanent, and is logged as validation rather than
    /// as parsing.
    /// </summary>
    [Fact]
    public async Task An_event_that_breaks_a_contract_rule_is_a_permanent_failure()
    {
        using var harness = new ProcessorHarness();

        var invalid = ValidEvent.Create() with
        {
            Data = ValidEvent.Data() with { AmountMinor = -1 },
        };

        var result = await harness.ProcessAsync(ProcessorHarness.Serialize(invalid), TestContext.Current.CancellationToken);

        Assert.Equal(MessageProcessingOutcome.PermanentFailure, result.Outcome);
        Assert.Equal(ValidationRule.NotPositive, result.Reason);
        Assert.Equal(nameof(ProcessingLog.MessageValidationFailed), LogEvent(harness));
        Assert.Equal(1, EmbeddedMetricsCapture.Count(harness.MetricRecord, MetricNames.ValidationFailures));
    }

    /// <summary>
    /// An event that parses but omits its whole data section is classified, not thrown.
    /// </summary>
    /// <remarks>
    /// The contract type is the parser's output, so a missing object arrives as null and the
    /// validator is what reports it. Reaching into it to build a log scope first turned an ordinary
    /// publisher mistake into a NullReferenceException that escaped the processor entirely — no
    /// result, no log event, no metric.
    /// </remarks>
    [Fact]
    public async Task An_event_missing_its_data_section_is_a_permanent_failure()
    {
        using var harness = new ProcessorHarness();

        var result = await harness.ProcessAsync(BodyWithoutData, TestContext.Current.CancellationToken);

        Assert.Equal(MessageProcessingOutcome.PermanentFailure, result.Outcome);
        Assert.Equal(nameof(ProcessingLog.MessageValidationFailed), LogEvent(harness));
        Assert.Equal(1, EmbeddedMetricsCapture.Count(harness.MetricRecord, MetricNames.ValidationFailures));
    }

    /// <summary>
    /// An event whose order identifier is missing is classified, not thrown.
    /// </summary>
    /// <remarks>
    /// The identity scope used to require one, so the message that most needed its validation failure
    /// logged was the one that could not produce it.
    /// </remarks>
    [Fact]
    public async Task An_event_missing_its_order_id_is_a_permanent_failure()
    {
        using var harness = new ProcessorHarness();

        var invalid = ValidEvent.Create() with { Data = ValidEvent.Data() with { OrderId = null! } };

        var result = await harness.ProcessAsync(
            ProcessorHarness.Serialize(invalid),
            TestContext.Current.CancellationToken);

        Assert.Equal(MessageProcessingOutcome.PermanentFailure, result.Outcome);
        Assert.Equal(nameof(ProcessingLog.MessageValidationFailed), LogEvent(harness));
        Assert.False(harness.LogLine.TryGetProperty(LogFields.OrderId, out _));
    }

    /// <summary>
    /// An identifier the publisher omitted is absent from the log, not zeroed.
    /// </summary>
    /// <remarks>
    /// The contract type's identifiers are not nullable, so an omitted one arrives as
    /// <see cref="Guid.Empty"/>. Rendering that writes an all-zeros identifier that reads as real:
    /// every uncorrelated event would share it, and a query for the records that truly had none would
    /// return nothing.
    /// </remarks>
    [Fact]
    public async Task An_omitted_correlation_id_is_not_logged_as_a_zeroed_guid()
    {
        using var harness = new ProcessorHarness();

        var withoutCorrelation = ValidEvent.Create() with { CorrelationId = Guid.Empty };

        await harness.ProcessAsync(
            ProcessorHarness.Serialize(withoutCorrelation),
            TestContext.Current.CancellationToken);

        Assert.False(harness.LogLine.TryGetProperty(LogFields.CorrelationId, out _));
    }

    /// <summary>
    /// A conflict is permanent and logs the hash whose scope refused the write.
    /// </summary>
    /// <remarks>
    /// The scope decides which of the two hashes is logged, and the hash is the single value the
    /// conflict runbook is read for. Asserting only that something was written would let the two be
    /// swapped — every order-scope conflict reporting the envelope hash — with the whole suite still
    /// green, so both scopes are pinned to the hash they compare.
    /// </remarks>
    [Theory]
    [InlineData(ConflictScope.Order, WriteFailureReason.BusinessHashMismatch)]
    [InlineData(ConflictScope.Event, WriteFailureReason.EnvelopeHashMismatch)]
    [InlineData(ConflictScope.TokenMismatch, WriteFailureReason.TokenMismatch)]
    public async Task A_conflict_reports_the_hash_its_scope_compared(ConflictScope scope, string reason)
    {
        using var harness = new ProcessorHarness();

        harness.StoreResult = new OrderWriteResult.Conflict(scope, reason);

        var result = await harness.ProcessAsync(ProcessorHarness.ValidBody(), TestContext.Current.CancellationToken);

        var hashes = new CanonicalPayloadHasher().ComputeHashes(ValidEvent.Create());

        // The token is the event identifier verbatim, so a token mismatch is envelope-level too.
        var expected = scope == ConflictScope.Order ? hashes.BusinessSha256 : hashes.EnvelopeSha256;

        Assert.Equal(MessageProcessingOutcome.PermanentFailure, result.Outcome);
        Assert.Equal(reason, result.Reason);
        Assert.Equal(nameof(ProcessingLog.IdempotencyConflict), LogEvent(harness));
        Assert.Equal(1, EmbeddedMetricsCapture.Count(harness.MetricRecord, MetricNames.IdempotencyConflicts));
        Assert.Equal(expected, harness.LogLine.GetProperty(LogFields.ComputedHash).GetString());
        Assert.NotEqual(hashes.BusinessSha256, hashes.EnvelopeSha256);
    }

    [Fact]
    public async Task A_transient_fault_is_returned_for_redelivery()
    {
        using var harness = new ProcessorHarness();

        harness.StoreResult = new OrderWriteResult.TransientFault(WriteFailureReason.Throttled);

        var result = await harness.ProcessAsync(ProcessorHarness.ValidBody(), TestContext.Current.CancellationToken);

        Assert.Equal(MessageProcessingOutcome.TransientFailure, result.Outcome);
        Assert.True(result.ShouldReportAsFailure);
        Assert.Equal(nameof(ProcessingLog.TransientProcessingFailure), LogEvent(harness));
        Assert.Equal(1, EmbeddedMetricsCapture.Count(harness.MetricRecord, MetricNames.TransientFailures));
    }

    /// <summary>
    /// A fault in this service is permanent, and is not reported as a retryable one.
    /// </summary>
    /// <remarks>
    /// A missing table or a denied action fails identically on every attempt. Reporting it as
    /// transient would spend the message's receive attempts and blame DynamoDB for a deployment
    /// defect.
    /// </remarks>
    [Fact]
    public async Task A_permanent_fault_is_not_reported_as_transient()
    {
        using var harness = new ProcessorHarness();

        harness.StoreResult = new OrderWriteResult.PermanentFault(WriteFailureReason.TableNotFound);

        var result = await harness.ProcessAsync(ProcessorHarness.ValidBody(), TestContext.Current.CancellationToken);

        Assert.Equal(MessageProcessingOutcome.PermanentFailure, result.Outcome);
        Assert.Equal(nameof(ProcessingLog.PermanentProcessingFailure), LogEvent(harness));
        Assert.Equal(1, EmbeddedMetricsCapture.Count(harness.MetricRecord, MetricNames.PermanentFaults));
        Assert.Equal(0, EmbeddedMetricsCapture.Count(harness.MetricRecord, MetricNames.TransientFailures));
    }

    /// <summary>
    /// The first-receipt gate reaches the metrics through the processor.
    /// </summary>
    /// <remarks>
    /// The gate lives in the publisher, and this is what proves the processor hands it the delivery
    /// number rather than a constant. Without it a redelivered poison message would produce a data
    /// point on every attempt, which is the amplification the gate exists to stop.
    /// </remarks>
    [Fact]
    public async Task A_redelivered_permanent_failure_publishes_no_further_data_point()
    {
        using var harness = new ProcessorHarness();

        var result = await harness.ProcessAsync(
            "{ not json",
            TestContext.Current.CancellationToken,
            approximateReceiveCount: 3);

        Assert.Equal(MessageProcessingOutcome.PermanentFailure, result.Outcome);
        Assert.False(EmbeddedMetricsCapture.DeclaresMetric(harness.MetricRecord, MetricNames.ValidationFailures));
        Assert.Equal(1, EmbeddedMetricsCapture.Count(harness.MetricRecord, MetricNames.BatchFailures));
    }

    /// <summary>
    /// A record is findable by the transport identifier before anything has parsed.
    /// </summary>
    [Fact]
    public async Task An_unparseable_message_is_still_queryable_by_its_message_id()
    {
        using var harness = new ProcessorHarness();

        var result = await harness.ProcessAsync("{ not json", TestContext.Current.CancellationToken);

        Assert.Equal(ProcessorHarness.MessageId, result.MessageId);
        Assert.Equal(
            ProcessorHarness.MessageId,
            harness.LogLine.GetProperty(LogFields.SqsMessageId).GetString());
        Assert.False(harness.LogLine.TryGetProperty(LogFields.OrderId, out _));
    }

    /// <summary>
    /// A processed message is queryable by order, event and correlation.
    /// </summary>
    [Fact]
    public async Task A_parsed_message_is_queryable_by_its_identifiers()
    {
        using var harness = new ProcessorHarness();

        await harness.ProcessAsync(ProcessorHarness.ValidBody(), TestContext.Current.CancellationToken);

        var expected = ValidEvent.Create();

        Assert.Equal(expected.EventId.ToString(), harness.LogLine.GetProperty(LogFields.EventId).GetString());
        Assert.Equal(expected.Data.OrderId, harness.LogLine.GetProperty(LogFields.OrderId).GetString());
        Assert.Equal(
            expected.CorrelationId.ToString(),
            harness.LogLine.GetProperty(LogFields.CorrelationId).GetString());
    }

    /// <summary>
    /// The token reaches the store.
    /// </summary>
    /// <remarks>
    /// Case 29 of the plan, on the processor's side. Cancellation means the invocation is ending, and
    /// a store that never sees the token cannot stop.
    /// </remarks>
    [Fact]
    public async Task The_cancellation_token_is_forwarded_to_the_store()
    {
        using var harness = new ProcessorHarness();
        using var cancellation = new CancellationTokenSource();

        await harness.ProcessAsync(ProcessorHarness.ValidBody(), cancellation.Token);

        Assert.Equal(cancellation.Token, harness.StoreToken);
    }

    /// <summary>
    /// A well-formed envelope with no <c>data</c> object at all.
    /// </summary>
    private const string BodyWithoutData = """
        {
          "schemaVersion": 1,
          "eventId": "0d76e91c-44e6-4fba-901f-bfdb76645299",
          "eventType": "OrderCreated",
          "occurredAtUtc": "2026-08-01T11:55:00+00:00",
          "source": "sample.order-publisher",
          "correlationId": "f1e02471-f9da-437f-bc32-e4e65394658a"
        }
        """;

    private static string LogEvent(ProcessorHarness harness) =>
        harness.LogLine.GetProperty("LogEvent").GetString()!;

}
