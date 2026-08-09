using ReliableOrders.Core.Observability;
using ReliableOrders.Core.Persistence;
using ReliableOrders.UnitTests.Observability;
using ReliableOrders.UnitTests.Validation;

namespace ReliableOrders.UnitTests.Processing;

/// <summary>
/// Cases 19 to 22 and 24: what a mixed batch returns, and what it does not.
/// </summary>
public sealed class SqsBatchHandlerTests
{
    /// <summary>
    /// Case 19 and 20. One failure is returned; the successes beside it are not.
    /// </summary>
    /// <remarks>
    /// The whole point of a partial batch response. Returning a successful record would have it
    /// redelivered and reprocessed, which idempotency survives but which spends receive attempts and
    /// eventually dead-letters a message that was committed on the first attempt.
    /// </remarks>
    [Fact]
    public async Task One_failed_record_does_not_return_the_successful_ones()
    {
        using var harness = new BatchHarness();

        harness.Outcomes["m-2"] = new OrderWriteResult.TransientFault(WriteFailureReason.Throttled);

        var response = await harness.HandleAsync(["m-1", "m-2", "m-3"]);

        Assert.Equal(["m-2"], BatchHarness.Identifiers(response));
    }

    /// <summary>
    /// Case 20, from the other side. A clean batch returns nothing.
    /// </summary>
    [Fact]
    public async Task A_batch_that_all_succeeded_returns_no_failures()
    {
        using var harness = new BatchHarness();

        var response = await harness.HandleAsync(["m-1", "m-2"]);

        Assert.Empty(response.BatchItemFailures);
    }

    /// <summary>
    /// A duplicate counts as a success and is acknowledged.
    /// </summary>
    /// <remarks>
    /// The story's own criterion, and the reason a replay storm costs nothing: every record resolves
    /// as a duplicate and the batch returns empty.
    /// </remarks>
    [Fact]
    public async Task A_duplicate_is_not_returned_as_a_failure()
    {
        using var harness = new BatchHarness();

        harness.Outcomes["m-1"] = new OrderWriteResult.Duplicate(DuplicateScope.Order);
        harness.Outcomes["m-2"] = new OrderWriteResult.Duplicate(DuplicateScope.Event);

        var response = await harness.HandleAsync(["m-1", "m-2"]);

        Assert.Empty(response.BatchItemFailures);
    }

    /// <summary>
    /// Case 21. The failure list names SQS message identifiers, not event identifiers.
    /// </summary>
    /// <remarks>
    /// Every record here carries the same event, so the domain event identifier is one value shared
    /// by all three. Returning that instead would produce an identifier Lambda matches against
    /// nothing, and the entire batch would replay.
    /// </remarks>
    [Fact]
    public async Task The_failure_list_carries_message_identifiers_not_event_identifiers()
    {
        using var harness = new BatchHarness();

        harness.Outcomes["m-2"] = new OrderWriteResult.TransientFault(WriteFailureReason.Throttled);

        var response = await harness.HandleAsync(["m-1", "m-2", "m-3"]);

        var eventId = ValidEvent.Create().EventId.ToString();

        Assert.Equal(["m-2"], BatchHarness.Identifiers(response));
        Assert.DoesNotContain(eventId, BatchHarness.Identifiers(response));
    }

    /// <summary>
    /// A permanent failure is returned too.
    /// </summary>
    /// <remarks>
    /// No retry can succeed, but acknowledging it would delete a message no one ever sees. Returning
    /// it spends the receive attempts and lands it on the dead-letter queue, where an operator can
    /// find it. Story 9.1's quarantine queue is what changes this.
    /// </remarks>
    [Fact]
    public async Task A_permanent_failure_is_returned_rather_than_acknowledged()
    {
        using var harness = new BatchHarness();

        harness.Bodies["m-1"] = "{ not json";

        var response = await harness.HandleAsync(["m-1", "m-2"]);

        Assert.Equal(["m-1"], BatchHarness.Identifiers(response));
    }

    /// <summary>
    /// One record throwing does not decide anything about the others.
    /// </summary>
    /// <remarks>
    /// Nothing below the handler is expected to throw, so this is the defect case. The record that
    /// threw is retried, the records beside it keep the outcomes they earned, and the invocation still
    /// returns a response — a handler that let the exception escape would report nothing and Lambda
    /// would replay successes already committed.
    /// </remarks>
    [Fact]
    public async Task One_record_that_throws_does_not_hide_the_others()
    {
        using var harness = new BatchHarness();

        harness.Throwing.Add("m-2");

        var response = await harness.HandleAsync(["m-1", "m-2", "m-3"]);

        // All three were attempted; only the one that threw is returned. The other two keep the
        // outcomes they earned rather than being replayed because of a defect beside them.
        Assert.Equal(["m-2"], BatchHarness.Identifiers(response));
        Assert.Equal(3, harness.ProcessedCount);
    }

    /// <summary>
    /// Cancellation is not a record failure.
    /// </summary>
    /// <remarks>
    /// It means the invocation is ending. Catching it would report a downstream fault that does not
    /// exist and hide why the batch stopped.
    /// </remarks>
    [Fact]
    public async Task Cancellation_propagates_rather_than_being_classified()
    {
        using var harness = new BatchHarness();

        harness.Cancelling.Add("m-1");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => harness.HandleAsync(["m-1", "m-2"]));
    }

    /// <summary>
    /// A client-side timeout is one record's failure, not the batch's.
    /// </summary>
    /// <remarks>
    /// <see cref="TaskCanceledException"/> derives from <see cref="OperationCanceledException"/>, and
    /// an AWS SDK HTTP timeout arrives as one with nothing cancelled. Rethrowing that type
    /// unconditionally would fail the invocation on a slow socket and have SQS redeliver every
    /// record, including the ones already committed — the exact replay this handler exists to
    /// prevent, caused by the guard meant to protect it.
    /// </remarks>
    [Fact]
    public async Task A_client_timeout_does_not_take_the_batch_down()
    {
        using var harness = new BatchHarness();

        harness.TimingOut.Add("m-2");

        var response = await harness.HandleAsync(["m-1", "m-2", "m-3"]);

        Assert.Equal(["m-2"], BatchHarness.Identifiers(response));
    }

    /// <summary>
    /// A record that failed unexpectedly is still findable by its message identifier.
    /// </summary>
    /// <remarks>
    /// The processor's own scope is disposed as the exception unwinds, so the handler has to reopen
    /// one. Without it the warning names no message, and a query for the record that failed returns
    /// nothing.
    /// </remarks>
    [Fact]
    public async Task A_record_that_threw_is_logged_against_its_message_id()
    {
        using var harness = new BatchHarness();

        harness.Throwing.Add("m-2");

        await harness.HandleAsync(["m-1", "m-2", "m-3"]);

        var line = harness.LogLines.Single(entry =>
            entry.GetProperty("LogEvent").GetString() == nameof(ProcessingLog.TransientProcessingFailure));

        Assert.Equal("m-2", line.GetProperty(LogFields.SqsMessageId).GetString());
    }

    /// <summary>
    /// A record that cannot be named is not counted as a returned failure either.
    /// </summary>
    /// <remarks>
    /// It is acknowledged rather than returned, so counting it would leave the metric claiming a
    /// failure the response does not contain and the completion line does not report.
    /// </remarks>
    [Fact]
    public async Task A_record_that_cannot_be_named_is_not_counted_as_a_batch_failure()
    {
        using var harness = new BatchHarness();

        await harness.HandleAsync(["m-1", ""]);

        Assert.Equal(0, EmbeddedMetricsCapture.Count(harness.MetricRecord, MetricNames.BatchFailures));
        Assert.Equal(0, harness.BatchCompletedLine.GetProperty(LogFields.FailureCount).GetInt64());
    }

    /// <summary>
    /// Case 24. Records past the deadline are returned unattempted.
    /// </summary>
    /// <remarks>
    /// The deadline is checked before each record rather than once for the batch, because it falls
    /// partway through and the records after it are the ones that must not be started. Nothing here
    /// reaches the processor.
    /// </remarks>
    [Fact]
    public async Task Records_past_the_deadline_are_deferred_unattempted()
    {
        using var harness = new BatchHarness();

        harness.Deadline = BatchHarness.Now - TimeSpan.FromSeconds(1);

        var response = await harness.HandleAsync(["m-1", "m-2"]);

        Assert.Equal(["m-1", "m-2"], BatchHarness.Identifiers(response));
        Assert.Equal(0, harness.ProcessedCount);
        Assert.Equal(2, EmbeddedMetricsCapture.Count(harness.MetricRecord, MetricNames.DeadlineDeferrals));
    }

    /// <summary>
    /// A deferral is counted apart from a transient fault.
    /// </summary>
    /// <remarks>
    /// One is back-pressure the handler applied to itself; the other is a downstream problem. Sharing
    /// a metric would leave an operator tuning a queue when the batch size is what is wrong.
    /// </remarks>
    [Fact]
    public async Task A_deferral_is_not_counted_as_a_transient_failure()
    {
        using var harness = new BatchHarness();

        harness.Deadline = BatchHarness.Now - TimeSpan.FromSeconds(1);

        await harness.HandleAsync(["m-1"]);

        Assert.Equal(0, EmbeddedMetricsCapture.Count(harness.MetricRecord, MetricNames.TransientFailures));
        Assert.Equal(1, EmbeddedMetricsCapture.Count(harness.MetricRecord, MetricNames.DeadlineDeferrals));
    }

    /// <summary>
    /// The batch reports its size and its failures whatever the outcome.
    /// </summary>
    /// <remarks>
    /// A partial batch failure returns a successful invocation, so Lambda's own error metric stays
    /// flat. These two are what say otherwise.
    /// </remarks>
    [Fact]
    public async Task The_batch_reports_its_size_and_failure_count()
    {
        using var harness = new BatchHarness();

        harness.Outcomes["m-2"] = new OrderWriteResult.TransientFault(WriteFailureReason.Throttled);

        await harness.HandleAsync(["m-1", "m-2", "m-3"]);

        Assert.Equal(3, EmbeddedMetricsCapture.Count(harness.MetricRecord, MetricNames.BatchSize));
        Assert.Equal(1, EmbeddedMetricsCapture.Count(harness.MetricRecord, MetricNames.BatchFailures));
        Assert.Equal("Warning", harness.BatchCompletedLine.GetProperty("LogLevel").GetString());
    }

    /// <summary>
    /// An empty batch is not an error.
    /// </summary>
    [Fact]
    public async Task An_empty_batch_returns_an_empty_response()
    {
        using var harness = new BatchHarness();

        var response = await harness.HandleAsync([]);

        Assert.Empty(response.BatchItemFailures);
        Assert.Equal(0, EmbeddedMetricsCapture.Count(harness.MetricRecord, MetricNames.BatchSize));
    }

    /// <summary>
    /// A record with no message identifier cannot be returned, and does not take the batch with it.
    /// </summary>
    /// <remarks>
    /// Unreachable against real SQS. If it happened, the record has no identifier to put in a failure
    /// list, and an entry Lambda cannot match would replay the whole batch including the successes —
    /// so it is reported and left acknowledged. That is the only outcome available rather than a good
    /// one, and the records beside it are unaffected.
    /// </remarks>
    [Fact]
    public async Task A_record_that_cannot_be_named_does_not_fail_the_batch()
    {
        using var harness = new BatchHarness();

        var response = await harness.HandleAsync(["m-1", ""]);

        Assert.Empty(response.BatchItemFailures);
        Assert.Equal(1, harness.ProcessedCount);
    }

    /// <summary>
    /// Every line the batch writes is queryable by the invocation.
    /// </summary>
    [Fact]
    public async Task Every_line_carries_the_lambda_request_id()
    {
        using var harness = new BatchHarness();

        await harness.HandleAsync(["m-1"]);

        Assert.All(
            harness.LogLines,
            line => Assert.Equal(
                BatchHarness.LambdaRequestId,
                line.GetProperty(LogFields.LambdaRequestId).GetString()));
    }
}
