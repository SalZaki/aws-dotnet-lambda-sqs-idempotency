using Microsoft.Extensions.Logging;
using ReliableOrders.Core.Persistence;
using ReliableOrders.Core.Validation;

namespace ReliableOrders.Core.Observability;

/// <summary>
/// Everything this service writes to its log, as one method per event.
/// </summary>
/// <remarks>
/// <para>
/// Most of the Do Not Log list in docs/observability.md is enforced here by what the methods will
/// accept. No method takes an exception, an attribute map, or a parsed event, so a caller holding a
/// DynamoDB item or a raw body has nowhere to put it. That is the point of routing every line through
/// one type: a rule that lives in a review checklist is followed until someone is in a hurry, whereas
/// one expressed as a signature is a compiler error. <c>TransactionCancellationClassifier</c> is given
/// no DynamoDB client for the same reason.
/// </para>
/// <para>
/// The limit of that guarantee is the <c>reason</c> and <c>computedHash</c> parameters, which are
/// strings. <c>WriteFailureReason</c> and <c>ParseFailureReason</c> are the vocabularies they are
/// meant to be drawn from, and both are low-cardinality by construction, but nothing stops a caller
/// passing an SDK exception message instead — that much is convention, and it is stated here rather
/// than left implied by a claim the signatures do not support. Making it structural means giving
/// those vocabularies a closed type of their own, which is a change to Epic 1 and Epic 2 rather than
/// to this file.
/// </para>
/// <para>
/// It also owns the vocabulary. Callers pass identifiers and durations; the event identifier, the
/// message template, the outcome name and the field names are chosen here, so two call sites cannot
/// describe one event differently and a saved query written against one of them keeps working.
/// </para>
/// <para>
/// Each event is a source-generated <c>LoggerMessage</c>, which is what CA1848 asks for and what the
/// <c>.editorconfig</c> note deferred to this story. On a per-record path the generated form is worth
/// having for a reason beyond allocation: the generator checks at compile time that every placeholder
/// in a template has a parameter behind it, so a template edited without its argument fails the build
/// instead of reaching CloudWatch as an unsubstituted <c>{OrderId}</c>. Each public method wraps a
/// private generated overload and supplies the outcome name itself, so no caller can name an outcome
/// the events do not agree with.
/// </para>
/// <para>
/// Not an interface. There is one implementation and no seam worth maintaining — a caller that wants
/// no output constructs this over a logger that discards it, and the tests that matter assert on what
/// reaches a provider rather than on a mock's recorded calls.
/// </para>
/// <para>
/// Every write reaches CloudWatch by way of stdout. Nothing here calls a CloudWatch API, per the
/// Logging Specification.
/// </para>
/// </remarks>
public sealed partial class ProcessingLog
{
    private readonly ILogger logger;
    private readonly string service;
    private readonly string environment;

    /// <summary>
    /// Creates a log for one execution environment.
    /// </summary>
    /// <param name="logger">Where lines are written.</param>
    /// <param name="service">The service name, constant for the process.</param>
    /// <param name="environment">The deployment environment, constant for the process.</param>
    public ProcessingLog(ILogger<ProcessingLog> logger, string service, string environment)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrWhiteSpace(service);
        ArgumentException.ThrowIfNullOrWhiteSpace(environment);

        this.logger = logger;
        this.service = service;
        this.environment = environment;
    }

    /// <summary>
    /// Opens the scope every line in one invocation carries.
    /// </summary>
    /// <remarks>
    /// Service and environment are constant for the process and could be attached once to the
    /// provider instead. They are put on this scope so the fields travel with the log records rather
    /// than with the configuration that produced them, which keeps a line self-describing when it is
    /// read outside the log group it came from.
    /// </remarks>
    /// <param name="lambdaRequestId">The invocation's request identifier.</param>
    /// <returns>The scope, which the caller disposes when the invocation ends.</returns>
    public IDisposable BeginInvocation(string lambdaRequestId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lambdaRequestId);

        return this.logger.BeginScope(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [LogFields.Service] = this.service,
            [LogFields.Environment] = this.environment,
            [LogFields.LambdaRequestId] = lambdaRequestId,
        }) ?? NullScope.Instance;
    }

    /// <summary>
    /// Opens the scope every line about one record carries.
    /// </summary>
    /// <remarks>
    /// The SQS message identifier is the only identifier that exists this early. A body that never
    /// parses produces no event or order identifier at all, so this scope is what makes a parse
    /// failure findable, and it is why the identity fields are a second scope rather than parameters
    /// here.
    /// </remarks>
    /// <param name="sqsMessageId">The SQS message identifier, never a domain event identifier.</param>
    /// <param name="approximateReceiveCount">Deliveries so far, counting this one.</param>
    /// <returns>The scope, which the caller disposes when the record is done.</returns>
    public IDisposable BeginRecord(string sqsMessageId, int approximateReceiveCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sqsMessageId);
        ArgumentOutOfRangeException.ThrowIfLessThan(approximateReceiveCount, 1);

        return this.logger.BeginScope(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [LogFields.SqsMessageId] = sqsMessageId,
            [LogFields.ApproximateReceiveCount] = approximateReceiveCount,
        }) ?? NullScope.Instance;
    }

    /// <summary>
    /// Adds the identifiers that exist only once a body has parsed.
    /// </summary>
    /// <remarks>
    /// Opened inside the record scope, immediately after parsing, so everything from validation
    /// onwards is queryable by order. The correlation identifier is optional in the contract, and an
    /// absent one is omitted rather than written as an empty string, so a query for lines lacking
    /// correlation finds the records that truly had none.
    /// </remarks>
    /// <param name="eventId">The domain event identifier, not a <see cref="LogEvents"/> number.</param>
    /// <param name="orderId">The order identifier.</param>
    /// <param name="correlationId">The publisher's correlation identifier, when it sent one.</param>
    /// <returns>The scope, which the caller disposes with the record.</returns>
    public IDisposable BeginOrderIdentity(string eventId, string orderId, string? correlationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
        ArgumentException.ThrowIfNullOrWhiteSpace(orderId);

        var state = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [LogFields.EventId] = eventId,
            [LogFields.OrderId] = orderId,
        };

        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            state[LogFields.CorrelationId] = correlationId;
        }

        return this.logger.BeginScope(state) ?? NullScope.Instance;
    }

    /// <summary>
    /// An invocation has begun.
    /// </summary>
    /// <param name="recordCount">How many records it received.</param>
    public void BatchStarted(int recordCount) => this.LogBatchStarted(recordCount);

    /// <summary>
    /// An invocation has finished.
    /// </summary>
    /// <remarks>
    /// Warning level when any record failed. A partial batch failure returns a successful invocation,
    /// so Lambda's own error metric stays flat and this line is the only place in the log where the
    /// invocation admits that something went wrong.
    /// </remarks>
    /// <param name="recordCount">How many records it received.</param>
    /// <param name="failureCount">How many are being returned as batch item failures.</param>
    /// <param name="duration">How long the invocation's record processing took.</param>
    public void BatchCompleted(int recordCount, int failureCount, TimeSpan duration) =>
        this.LogBatchCompleted(
            failureCount > 0 ? LogLevel.Warning : LogLevel.Information,
            failureCount,
            recordCount,
            Milliseconds(duration));

    /// <summary>
    /// A body could not be turned into an order event.
    /// </summary>
    /// <param name="reason">A value from <see cref="Contracts.ParseFailureReason"/>.</param>
    /// <param name="duration">How long the record took.</param>
    public void MessageParsingFailed(string reason, TimeSpan duration) =>
        this.LogMessageParsingFailed(reason, Milliseconds(duration), PermanentFailureOutcome);

    /// <summary>
    /// A parsed event broke one or more contract rules.
    /// </summary>
    /// <remarks>
    /// Takes the result rather than a formatted string so the offending values cannot travel with it.
    /// A <see cref="ValidationFailure"/> holds a field path and a rule name, both fixed vocabulary,
    /// and never what the publisher actually sent.
    /// </remarks>
    /// <param name="result">The failing result. Its failures are already in a stable order.</param>
    /// <param name="duration">How long the record took.</param>
    public void MessageValidationFailed(ValidationResult result, TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(result);

        this.LogMessageValidationFailed(DescribeFailures(result), Milliseconds(duration), PermanentFailureOutcome);
    }

    /// <summary>
    /// An order and its idempotency record were written by this attempt.
    /// </summary>
    /// <param name="duration">How long the record took.</param>
    public void OrderCreated(TimeSpan duration) =>
        this.LogOrderCreated(Milliseconds(duration), ProcessedOutcome);

    /// <summary>
    /// The work was already done and the stored data agreed.
    /// </summary>
    /// <remarks>
    /// Information, not warning. Duplicates are the expected consequence of at-least-once delivery and
    /// a replay storm is correct behaviour, so raising the level here would train operators to ignore
    /// it well before an alarm has anything to say.
    /// </remarks>
    /// <param name="scope">Which safeguard recognised it.</param>
    /// <param name="duration">How long the record took.</param>
    public void DuplicateIgnored(DuplicateScope scope, TimeSpan duration) =>
        this.LogDuplicateIgnored(scope, Milliseconds(duration), DuplicateOutcome);

    /// <summary>
    /// One identity is claimed by two payloads that disagree.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The specification's rule for a condition-check failure is to log the compared hashes and never
    /// the returned item, and this signature is how that is held to. The item exists only inside
    /// <c>TransactionCancellationClassifier</c>, which reads the one attribute it needs and discards
    /// the rest, so no item can reach a caller of this method.
    /// </para>
    /// <para>
    /// Only the computed hash is logged, because it is the only one the classifier keeps: a conflict
    /// is reported as a scope and a reason, not as the pair of values that differed. The scope already
    /// names which hash diverged, which is what an operator needs first, and carrying the stored hash
    /// out of the classifier means widening <see cref="OrderWriteResult.Conflict"/>. That belongs with
    /// the conflict runbook in Story 5.4 rather than here, and is written down so the gap stays
    /// visible rather than looking like an oversight.
    /// </para>
    /// </remarks>
    /// <param name="scope">Which safeguard refused it.</param>
    /// <param name="reason">A value from <see cref="WriteFailureReason"/>.</param>
    /// <param name="computedHash">The hash this event produced, as hexadecimal.</param>
    /// <param name="duration">How long the record took.</param>
    public void IdempotencyConflict(ConflictScope scope, string reason, string computedHash, TimeSpan duration) =>
        this.LogIdempotencyConflict(scope, reason, computedHash, Milliseconds(duration), PermanentFailureOutcome);

    /// <summary>
    /// The attempt failed for a reason that may not recur, and the record will be redelivered.
    /// </summary>
    /// <param name="reason">A value from <see cref="WriteFailureReason"/>.</param>
    /// <param name="duration">How long the record took.</param>
    public void TransientProcessingFailure(string reason, TimeSpan duration) =>
        this.LogTransientProcessingFailure(reason, Milliseconds(duration), TransientFailureOutcome);

    /// <summary>
    /// The request was one the store will never accept, which is a fault in this service.
    /// </summary>
    /// <remarks>
    /// Error rather than warning, unlike the two permanent failures a publisher causes. A malformed
    /// request, a missing table or a denied action is a defect in this service or its deployment, and
    /// no publisher can fix it.
    /// </remarks>
    /// <param name="reason">A value from <see cref="WriteFailureReason"/>.</param>
    /// <param name="duration">How long the record took.</param>
    public void PermanentProcessingFailure(string reason, TimeSpan duration) =>
        this.LogPermanentProcessingFailure(reason, Milliseconds(duration), PermanentFailureOutcome);

    /// <summary>
    /// A record was returned unattempted because too little invocation time remained.
    /// </summary>
    /// <remarks>
    /// The one terminal event with no <c>DurationMs</c>. No work was done, so there is no duration to
    /// report, and writing a zero would drag the latency a query derives from this field down exactly
    /// when the handler is under the most pressure. <c>RemainingMs</c> is what this event is read for.
    /// The metrics side excludes deferrals from latency for the same reason.
    /// </remarks>
    /// <param name="remaining">Invocation time left when the record was deferred.</param>
    public void ProcessingDeadlineReached(TimeSpan remaining) =>
        this.LogProcessingDeadlineReached(Milliseconds(remaining), DeadlineDeferredOutcome);

    // The generated overloads. Prefixed rather than overloaded so a call site reads as one or the
    // other, and named through nameof so a rename of the public method carries the log event's name
    // with it — an operator's saved query breaks loudly at the build rather than quietly at run time.
    [LoggerMessage(
        EventId = LogEvents.BatchStarted,
        EventName = nameof(BatchStarted),
        Level = LogLevel.Information,
        Message = "Batch started with {RecordCount} records")]
    private partial void LogBatchStarted(int recordCount);

    [LoggerMessage(
        EventId = LogEvents.BatchCompleted,
        EventName = nameof(BatchCompleted),
        Message = "Batch completed with {FailureCount} of {RecordCount} records failed in {DurationMs}ms")]
    private partial void LogBatchCompleted(LogLevel level, int failureCount, int recordCount, long durationMs);

    [LoggerMessage(
        EventId = LogEvents.MessageParsingFailed,
        EventName = nameof(MessageParsingFailed),
        Level = LogLevel.Warning,
        Message = "Message parsing failed with {Reason} after {DurationMs}ms, outcome {Outcome}")]
    private partial void LogMessageParsingFailed(string reason, long durationMs, string outcome);

    [LoggerMessage(
        EventId = LogEvents.MessageValidationFailed,
        EventName = nameof(MessageValidationFailed),
        Level = LogLevel.Warning,
        Message = "Message validation failed on {FailedRules} after {DurationMs}ms, outcome {Outcome}")]
    private partial void LogMessageValidationFailed(string failedRules, long durationMs, string outcome);

    [LoggerMessage(
        EventId = LogEvents.OrderCreated,
        EventName = nameof(OrderCreated),
        Level = LogLevel.Information,
        Message = "Order created in {DurationMs}ms, outcome {Outcome}")]
    private partial void LogOrderCreated(long durationMs, string outcome);

    [LoggerMessage(
        EventId = LogEvents.DuplicateIgnored,
        EventName = nameof(DuplicateIgnored),
        Level = LogLevel.Information,
        Message = "Duplicate ignored at {Scope} scope in {DurationMs}ms, outcome {Outcome}")]
    private partial void LogDuplicateIgnored(DuplicateScope scope, long durationMs, string outcome);

    [LoggerMessage(
        EventId = LogEvents.IdempotencyConflict,
        EventName = nameof(IdempotencyConflict),
        Level = LogLevel.Error,
        Message = "Idempotency conflict at {Scope} scope with {Reason}, computed {ComputedHash}, "
            + "after {DurationMs}ms, outcome {Outcome}")]
    private partial void LogIdempotencyConflict(
        ConflictScope scope,
        string reason,
        string computedHash,
        long durationMs,
        string outcome);

    [LoggerMessage(
        EventId = LogEvents.TransientProcessingFailure,
        EventName = nameof(TransientProcessingFailure),
        Level = LogLevel.Warning,
        Message = "Transient processing failure with {Reason} after {DurationMs}ms, outcome {Outcome}")]
    private partial void LogTransientProcessingFailure(string reason, long durationMs, string outcome);

    [LoggerMessage(
        EventId = LogEvents.PermanentProcessingFailure,
        EventName = nameof(PermanentProcessingFailure),
        Level = LogLevel.Error,
        Message = "Permanent processing failure with {Reason} after {DurationMs}ms, outcome {Outcome}")]
    private partial void LogPermanentProcessingFailure(string reason, long durationMs, string outcome);

    [LoggerMessage(
        EventId = LogEvents.ProcessingDeadlineReached,
        EventName = nameof(ProcessingDeadlineReached),
        Level = LogLevel.Warning,
        Message = "Processing deadline reached with {RemainingMs}ms remaining, outcome {Outcome}")]
    private partial void LogProcessingDeadlineReached(long remainingMs, string outcome);

    private const string ProcessedOutcome = "Processed";
    private const string DuplicateOutcome = "Duplicate";
    private const string PermanentFailureOutcome = "PermanentFailure";
    private const string TransientFailureOutcome = "TransientFailure";
    private const string DeadlineDeferredOutcome = "DeadlineDeferred";

    /// <remarks>
    /// Rounded to whole milliseconds. Sub-millisecond precision on a duration dominated by a network
    /// round trip is noise, and a whole number keeps the field comparable across the log, the metric,
    /// and the span that report the same work.
    /// </remarks>
    private static long Milliseconds(TimeSpan duration) => (long)Math.Round(duration.TotalMilliseconds);

    /// <remarks>
    /// Field and rule joined, comma-separated, in the order the validator produced. One string rather
    /// than a nested array because CloudWatch Logs Insights cannot group by an array member, and
    /// grouping failures by their pattern is the question this field exists to answer.
    /// </remarks>
    private static string DescribeFailures(ValidationResult result) =>
        string.Join(",", result.Failures.Select(failure => $"{failure.Field}:{failure.Rule}"));

    /// <summary>
    /// Stands in for a scope when the provider declines to open one.
    /// </summary>
    /// <remarks>
    /// <see cref="ILogger.BeginScope{TState}"/> is documented as returning null when scopes are
    /// disabled, and every caller here disposes what it is given inside a <c>using</c>. Returning this
    /// instead of a null keeps the handler free of null checks around something it only disposes.
    /// </remarks>
    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        private NullScope()
        {
        }

        public void Dispose()
        {
        }
    }
}
