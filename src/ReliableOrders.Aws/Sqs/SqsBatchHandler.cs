using System.Diagnostics;
using Amazon.Lambda.SQSEvents;
using ReliableOrders.Aws.Telemetry;
using ReliableOrders.Core.Observability;
using ReliableOrders.Core.Persistence;
using ReliableOrders.Core.Processing;

namespace ReliableOrders.Aws.Sqs;

/// <summary>
/// Processes one SQS batch and reports which of its records failed.
/// </summary>
/// <remarks>
/// <para>
/// Records are processed sequentially, and deliberately so until the correctness tests and metrics
/// this story depends on have run against something real. Bounded parallelism is a later change; the
/// accumulator and the writer it publishes through are already guarded for it.
/// </para>
/// <para>
/// Independence is the point. One record's failure — including one that throws where nothing should
/// — must not decide anything about the other nine, so every record is processed inside its own
/// try/catch and every result is collected before the response is built. A handler that let one
/// exception escape would report nothing, and Lambda would replay a batch whose successes had
/// already been committed.
/// </para>
/// <para>
/// What it will not do is swallow cancellation. That means the invocation is ending rather than that
/// a record failed, and classifying it as a transient fault would report a downstream problem that
/// does not exist while hiding the reason the batch stopped.
/// </para>
/// </remarks>
public sealed class SqsBatchHandler
{
    private readonly IOrderMessageProcessor _processor;
    private readonly EmbeddedMetricsPublisher _metrics;
    private readonly ProcessingLog _log;
    private readonly TimeProvider _clock;

    /// <summary>
    /// Creates a handler for one execution environment.
    /// </summary>
    /// <param name="processor">Processes one message.</param>
    /// <param name="metrics">Opens the invocation's metrics accumulator.</param>
    /// <param name="log">Where the batch and its records are reported.</param>
    /// <param name="clock">Reads the current instant, to compare against the deadline.</param>
    public SqsBatchHandler(
        IOrderMessageProcessor processor,
        EmbeddedMetricsPublisher metrics,
        ProcessingLog log,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(processor);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(clock);

        _processor = processor;
        _metrics = metrics;
        _log = log;
        _clock = clock;
    }

    /// <summary>
    /// Processes every record in the batch.
    /// </summary>
    /// <param name="batch">The event as the runtime deserialised it.</param>
    /// <param name="invocation">What this invocation knows about itself.</param>
    /// <param name="cancellationToken">Forwarded to each record.</param>
    /// <returns>
    /// The records to redeliver, by SQS message identifier. Empty when every record succeeded, which
    /// is not the same as null — see the Composition Root section of docs/architecture.md for what an
    /// unregistered response type does to this value.
    /// </returns>
    public async Task<SQSBatchResponse> HandleAsync(
        SQSEvent batch,
        BatchInvocation invocation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(invocation);

        var records = batch.Records ?? [];
        var started = _clock.GetTimestamp();

        using var scope = _log.BeginInvocation(invocation.LambdaRequestId);
        using var metrics = _metrics.BeginInvocation(records.Count);

        _log.BatchStarted(records.Count);

        var failed = new List<string?>(records.Count);

        foreach (var record in records)
        {
            var outcome = await ProcessRecordAsync(record, invocation, metrics, cancellationToken)
                .ConfigureAwait(false);

            if (outcome is not null)
            {
                failed.Add(outcome);
            }
        }

        _log.BatchCompleted(records.Count, failed.Count, _clock.GetElapsedTime(started));

        return BatchItemFailures.From(failed);
    }

    /// <returns>
    /// The message identifier to redeliver, or null when the record needs no redelivery. Null also
    /// covers the record that cannot be named, which is explained where that is caught.
    /// </returns>
    private async Task<string?> ProcessRecordAsync(
        SQSEvent.SQSMessage record,
        BatchInvocation invocation,
        IInvocationMetrics metrics,
        CancellationToken cancellationToken)
    {
        IncomingMessage message;

        try
        {
            message = record.ToIncomingMessage();
        }
        catch (ArgumentException)
        {
            // A record SQS did not give a message identifier. It cannot be returned as a batch item
            // failure, because a failure list entry is a message identifier and there is none — and an
            // entry Lambda does not recognise would replay the whole batch, including the records that
            // succeeded. So it is reported and left acknowledged, which is the only outcome available
            // rather than the preferred one. Unreachable against real SQS; the mapper and
            // IncomingMessage both reject it, and this exists because the alternative to handling it
            // is losing the whole batch to an exception.
            //
            // Logged and not counted. Every permanent-outcome metric also increments BatchFailures,
            // which is documented as how many records are being returned — and this one is not. A
            // metric saying one record failed while the response list is empty and BatchCompleted logs
            // none would leave the three reports contradicting each other over a case that cannot
            // happen. The event is Error level, which is the signal.
            _log.PermanentProcessingFailure(WriteFailureReason.MalformedRequest, TimeSpan.Zero);

            return null;
        }

        if (_clock.GetUtcNow() >= invocation.Deadline)
        {
            return Defer(message, invocation, metrics);
        }

        // Started after the deadline check, so a deferred record produces no span. It was never
        // attempted, and a span saying otherwise would put a zero-length process step in the
        // publisher's trace for work this invocation declined to do. The deferral is reported as a log
        // event and a metric, which is where a deferral belongs.
        //
        // Disposed at the end of the record either way, including when the processor throws, which is
        // what the using gives us — an undisposed activity is never exported and the record would
        // vanish from the trace exactly when something went wrong.
        using var span = RecordTrace.Start(message, invocation.LambdaRequestId);

        var started = _clock.GetTimestamp();

        try
        {
            var result = await _processor.ProcessAsync(message, metrics, cancellationToken)
                .ConfigureAwait(false);

            return result.ShouldReportAsFailure ? result.MessageId : null;
        }
        // Only cancellation of *this* token means the invocation is ending. TaskCanceledException
        // derives from OperationCanceledException, and an AWS SDK client-side HTTP timeout arrives as
        // one — so an unfiltered rethrow would let a socket timeout on record seven escape, fail the
        // invocation, and have SQS redeliver all ten records including the six already committed.
        // That is precisely the replay this handler exists to prevent, caused by the guard meant to
        // protect it. Anything not cancelled by this token falls through and is returned as one
        // record's failure.
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Errored, with no outcome attribute. The record reached none — the invocation ended
            // underneath it — and inventing a value outside the fixed vocabulary to fill the gap would
            // put a word in traces that no log line or metric ever uses. The status alone says the
            // record was abandoned, which is what happened.
            span?.SetStatus(ActivityStatusCode.Error, "the invocation ended before the record finished");

            throw;
        }
#pragma warning disable CA1031 // One record's defect may not decide the other nine's outcome.
        catch (Exception)
#pragma warning restore CA1031
        {
            // Nothing below here is expected to throw: the processor classifies what it can and the
            // store reports failure as a value. Reaching this means a defect, and the safe direction
            // is to retry — a record returned that need not have been resolves as a duplicate, while
            // one acknowledged after an unexplained failure is gone. The exception itself is not
            // logged, because the Do Not Log list covers exception payloads and the formatter would
            // drop the message anyway.
            //
            // The scope is reopened because the processor's own was disposed as the exception
            // unwound, and a warning an operator cannot attribute to a message is a warning about
            // nothing. The duration is measured rather than zeroed: the record spent that time, and a
            // false zero drags down the latency distribution exactly when something is wrong.
            var duration = _clock.GetElapsedTime(started);

            using var scope = _log.BeginRecord(message.MessageId, message.ApproximateReceiveCount);

            _log.TransientProcessingFailure(WriteFailureReason.ServiceUnavailable, duration);
            metrics.TransientFailure(duration);

            // Marked here because the processor never got to. It writes the outcome after the work
            // returns, and this path is the work not returning — so without this the record that
            // failed for an unexplained reason is the one span an operator filtering for errors would
            // not find, while the log line and the metric both call it a failure.
            span?.SetTag(Tracing.Attributes.Outcome, nameof(MessageProcessingOutcome.TransientFailure));
            span?.SetTag(Tracing.Attributes.Reason, WriteFailureReason.ServiceUnavailable);
            span?.SetStatus(ActivityStatusCode.Error, WriteFailureReason.ServiceUnavailable);

            return message.MessageId;
        }
    }

    /// <remarks>
    /// Checked before each record rather than once for the batch, because the deadline is reached
    /// partway through and the records after it are the ones that must not be started.
    /// </remarks>
    private string Defer(IncomingMessage message, BatchInvocation invocation, IInvocationMetrics metrics)
    {
        using var scope = _log.BeginRecord(message.MessageId, message.ApproximateReceiveCount);

        _log.ProcessingDeadlineReached(_clock.GetUtcNow() - invocation.Deadline);
        metrics.DeadlineDeferral();

        return message.MessageId;
    }
}
