using System.Diagnostics;
using ReliableOrders.Core.Contracts;
using ReliableOrders.Core.Idempotency;
using ReliableOrders.Core.Observability;
using ReliableOrders.Core.Persistence;
using ReliableOrders.Core.Validation;

namespace ReliableOrders.Core.Processing;

/// <summary>
/// Parses, validates, hashes and persists one message, reporting each outcome exactly once.
/// </summary>
/// <remarks>
/// <para>
/// The order of the steps is the correctness model. Parsing before validation keeps a malformed body
/// and a negative amount classified apart; validation before hashing means the contract rules — a
/// zero UTC offset above all — hold by the time a hash is computed, so the same event always hashes
/// the same way; hashing before persistence is what the transaction compares against. Nothing here
/// may be reordered without reading
/// <c>docs/correctness-model.md</c> first.
/// </para>
/// <para>
/// Every outcome leaves by one path: a log event, a metric, and a result that all name the same
/// thing. They are written next to each other rather than in three passes so a new outcome cannot be
/// added to one and forgotten in the others — the compiler enforces the third through
/// <see cref="OrderWriteResult.Match{TResult}"/>, and proximity is what covers the first two.
/// </para>
/// <para>
/// Nothing is caught. The store reports failure by returning a value, the parser and validator do not
/// throw for bad input, and an exception from anywhere else is a defect rather than a message this
/// can classify — the batch handler is what keeps one such defect from hiding the other records'
/// results. Cancellation propagates untouched, because the invocation ending is not a downstream
/// fault and reporting it as one would misattribute it.
/// </para>
/// </remarks>
public sealed class OrderMessageProcessor : IOrderMessageProcessor
{
    private readonly IOrderEventParser _parser;
    private readonly IOrderEventValidator _validator;
    private readonly IPayloadHasher _hasher;
    private readonly IOrderCommandStore _store;
    private readonly ProcessingLog _log;
    private readonly TimeProvider _clock;

    /// <summary>
    /// Creates a processor for one execution environment.
    /// </summary>
    /// <param name="parser">Reads a body into a typed event.</param>
    /// <param name="validator">Checks the event against the contract rules.</param>
    /// <param name="hasher">Computes the two idempotency hashes.</param>
    /// <param name="store">Creates the order and claims the event, atomically.</param>
    /// <param name="log">Where every outcome is reported.</param>
    /// <param name="clock">
    /// Measures how long a message took, and nothing else. No value this writes derives from it —
    /// the determinism rule keeps a clock out of the transaction, and the rule holds because the
    /// store takes none.
    /// </param>
    public OrderMessageProcessor(
        IOrderEventParser parser,
        IOrderEventValidator validator,
        IPayloadHasher hasher,
        IOrderCommandStore store,
        ProcessingLog log,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(parser);
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(hasher);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(clock);

        _parser = parser;
        _validator = validator;
        _hasher = hasher;
        _store = store;
        _log = log;
        _clock = clock;
    }

    /// <inheritdoc/>
    public async Task<MessageProcessingResult> ProcessAsync(
        IncomingMessage message,
        IInvocationMetrics metrics,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(metrics);

        var started = _clock.GetTimestamp();

        // The record's span, started by whatever delivered the message, or null when nothing is
        // listening. Captured here rather than read from Activity.Current further down, where the step
        // spans below are current instead and an attribute meant for the record would land on one of
        // them. Nothing is started here: this layer knows no transport, and a span covering the record
        // needs the message identifier and the publisher's trace context, which only the transport has.
        var span = Activity.Current;

        // Opened before anything can fail, because the message identifier is the only thing a body
        // that never parses leaves behind to find it by.
        using var record = _log.BeginRecord(message.MessageId, message.ApproximateReceiveCount);

        // One Match, with the parsed branch returning the task the rest of the work runs on. Matching
        // twice — once for a failure reason and again for the event — would give up the exhaustiveness
        // this exists for, and would need a null in each branch that does not apply.
        var result = await Parse(message.Body).Match(
            whenParsed: value =>
                ProcessEventAsync(message, metrics, started, value.Event, span, cancellationToken),
            whenMalformed: malformed =>
                Task.FromResult(ParseFailed(message, metrics, started, malformed.Reason)),
            whenUnsupportedSchemaVersion: _ =>
                Task.FromResult(ParseFailed(
                    message, metrics, started, ParseFailureReason.UnsupportedSchemaVersion)))
            .ConfigureAwait(false);

        Describe(span, result);

        return result;
    }

    /// <remarks>
    /// The span wraps the parser rather than the whole branch, because the branch includes the work
    /// that follows a successful parse and a span named for parsing should not cover persistence.
    /// </remarks>
    private ParseResult Parse(string body)
    {
        using var span = Tracing.Source.StartActivity(Tracing.Spans.Parse);

        return _parser.Parse(body);
    }

    /// <inheritdoc cref="Parse"/>
    private ValidationResult Validate(OrderCreatedV1 orderEvent)
    {
        using var span = Tracing.Source.StartActivity(Tracing.Spans.Validate);

        return _validator.Validate(orderEvent);
    }

    /// <remarks>
    /// Traced despite being the one step here that touches nothing outside the process. Canonical
    /// hashing re-serialises the event, and a latency distribution that shows it growing with payload
    /// size is the evidence a future change to the canonical form would need.
    /// </remarks>
    private PayloadHashes Hash(OrderCreatedV1 orderEvent)
    {
        using var span = Tracing.Source.StartActivity(Tracing.Spans.Hash);

        return _hasher.ComputeHashes(orderEvent);
    }

    /// <remarks>
    /// Each value is written only when it is there. This runs on the parser's output, so an event that
    /// is about to fail validation can be missing any of them, and an attribute set to null is a
    /// removal rather than an empty value.
    /// </remarks>
    private static void Identify(Activity? span, OrderCreatedV1 orderEvent)
    {
        if (span is null)
        {
            return;
        }

        span.SetTag(Tracing.Attributes.EventId, Identifier(orderEvent.EventId));
        span.SetTag(Tracing.Attributes.OrderId, OrderIdentifier(orderEvent.Data?.OrderId));
        span.SetTag(Tracing.Attributes.CorrelationId, Identifier(orderEvent.CorrelationId));
    }

    /// <summary>
    /// Records what the record concluded on its span.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One place rather than one per terminal branch, because the result already carries both values
    /// and the branches would otherwise each have to remember to annotate. The vocabulary is the
    /// logs': the same outcome names and the same fixed reason strings, so a trace and a log line
    /// about one record agree rather than describing it in two dialects.
    /// </para>
    /// <para>
    /// The status is set from whether the record is being returned, not from whether it succeeded. A
    /// duplicate is neither an error nor worth marking as one — the mechanism worked — while a
    /// conflict, an invalid event and a transient fault are all things an operator searching for
    /// errored spans should find.
    /// </para>
    /// </remarks>
    private static void Describe(Activity? span, MessageProcessingResult result)
    {
        if (span is null)
        {
            return;
        }

        span.SetTag(Tracing.Attributes.Outcome, result.Outcome.ToString());

        if (result.Reason is not null)
        {
            span.SetTag(Tracing.Attributes.Reason, result.Reason);
        }

        span.SetStatus(
            result.ShouldReportAsFailure ? ActivityStatusCode.Error : ActivityStatusCode.Ok,
            result.Reason);
    }

    /// <remarks>
    /// Reached once the body is a known event, so the identifiers join the scope here and every line
    /// from validation onwards is queryable by them. The scope flows across the await, because a
    /// logging scope is ambient to the asynchronous context rather than to the thread.
    /// </remarks>
    private async Task<MessageProcessingResult> ProcessEventAsync(
        IncomingMessage message,
        IInvocationMetrics metrics,
        long started,
        OrderCreatedV1 orderEvent,
        Activity? span,
        CancellationToken cancellationToken)
    {
        // Every value here comes from parsing, not validation, so any of them can be absent: this type
        // is the parser's output and a missing field arrives as null or as a zeroed identifier. The
        // scope takes what is there and omits the rest, because a message whose order identifier is
        // missing is exactly the message that most needs its validation failure logged rather than
        // replaced by an exception from the logging call.
        using var identity = _log.BeginOrderIdentity(
            Identifier(orderEvent.EventId),
            OrderIdentifier(orderEvent.Data?.OrderId),
            Identifier(orderEvent.CorrelationId));

        // The same three identifiers the log scope just took, on the record's span. Written here
        // rather than by the transport because they exist only once a body has parsed, and written as
        // attributes rather than as a span name because a name carrying an identifier makes every
        // aggregate view one row per order.
        Identify(span, orderEvent);

        var validation = Validate(orderEvent);

        if (!validation.IsValid)
        {
            return ValidationFailed(message, metrics, started, validation);
        }

        var hashes = Hash(orderEvent);

        var written = await _store.TryCreateAsync(orderEvent, hashes, cancellationToken).ConfigureAwait(false);

        return written.Match(
            whenCreated: _ => Created(message, metrics, started),
            whenDuplicate: duplicate => Duplicate(message, metrics, started, duplicate),
            whenConflict: conflict => Conflict(message, metrics, started, conflict, hashes),
            whenTransientFault: fault => Transient(message, metrics, started, fault),
            whenPermanentFault: fault => Permanent(message, metrics, started, fault));
    }

    /// <remarks>
    /// A body that would not parse. Counted as a validation failure because the operational response
    /// is the same — look at the publisher — while the log keeps the two events apart, because an
    /// operator diagnosing one message wants to know which it was.
    /// </remarks>
    private MessageProcessingResult ParseFailed(
        IncomingMessage message,
        IInvocationMetrics metrics,
        long started,
        string reason)
    {
        var duration = Elapsed(started);

        _log.MessageParsingFailed(reason, duration);
        metrics.InvalidEvent(message.ApproximateReceiveCount, duration);

        return new MessageProcessingResult(
            message.MessageId,
            MessageProcessingOutcome.PermanentFailure,
            reason,
            duration);
    }

    /// <remarks>
    /// An event that parsed and broke a contract rule. Every failing rule reaches the log as a field
    /// and rule pair; the result carries the first, which is what a batch summary can hold without
    /// becoming a second copy of the log line.
    /// </remarks>
    private MessageProcessingResult ValidationFailed(
        IncomingMessage message,
        IInvocationMetrics metrics,
        long started,
        ValidationResult validation)
    {
        var duration = Elapsed(started);

        _log.MessageValidationFailed(validation, duration);
        metrics.InvalidEvent(message.ApproximateReceiveCount, duration);

        return new MessageProcessingResult(
            message.MessageId,
            MessageProcessingOutcome.PermanentFailure,
            validation.Failures[0].Rule,
            duration);
    }

    private MessageProcessingResult Created(IncomingMessage message, IInvocationMetrics metrics, long started)
    {
        var duration = Elapsed(started);

        _log.OrderCreated(duration);
        metrics.OrderProcessed(duration);

        return new MessageProcessingResult(
            message.MessageId,
            MessageProcessingOutcome.Processed,
            Reason: null,
            duration);
    }

    private MessageProcessingResult Duplicate(
        IncomingMessage message,
        IInvocationMetrics metrics,
        long started,
        OrderWriteResult.Duplicate duplicate)
    {
        var duration = Elapsed(started);

        _log.DuplicateIgnored(duplicate.Scope, duration);
        metrics.DuplicateEvent(duration);

        return new MessageProcessingResult(
            message.MessageId,
            MessageProcessingOutcome.Duplicate,
            duplicate.Scope.ToString(),
            duration);
    }

    /// <remarks>
    /// The hash logged is the one this event computed. The stored value that disagreed with it never
    /// leaves the classifier, which is what keeps a returned DynamoDB item out of the log.
    /// </remarks>
    private MessageProcessingResult Conflict(
        IncomingMessage message,
        IInvocationMetrics metrics,
        long started,
        OrderWriteResult.Conflict conflict,
        PayloadHashes hashes)
    {
        var duration = Elapsed(started);

        var computed = conflict.Scope == ConflictScope.Order ? hashes.BusinessSha256 : hashes.EnvelopeSha256;

        _log.IdempotencyConflict(conflict.Scope, conflict.Reason, computed, duration);
        metrics.IdempotencyConflict(message.ApproximateReceiveCount, duration);

        return new MessageProcessingResult(
            message.MessageId,
            MessageProcessingOutcome.PermanentFailure,
            conflict.Reason,
            duration);
    }

    private MessageProcessingResult Transient(
        IncomingMessage message,
        IInvocationMetrics metrics,
        long started,
        OrderWriteResult.TransientFault fault)
    {
        var duration = Elapsed(started);

        _log.TransientProcessingFailure(fault.Reason, duration);
        metrics.TransientFailure(duration);

        return new MessageProcessingResult(
            message.MessageId,
            MessageProcessingOutcome.TransientFailure,
            fault.Reason,
            duration);
    }

    private MessageProcessingResult Permanent(
        IncomingMessage message,
        IInvocationMetrics metrics,
        long started,
        OrderWriteResult.PermanentFault fault)
    {
        var duration = Elapsed(started);

        _log.PermanentProcessingFailure(fault.Reason, duration);
        metrics.PermanentFault(message.ApproximateReceiveCount, duration);

        return new MessageProcessingResult(
            message.MessageId,
            MessageProcessingOutcome.PermanentFailure,
            fault.Reason,
            duration);
    }

    private TimeSpan Elapsed(long started) => _clock.GetElapsedTime(started);

    /// <remarks>
    /// An identifier the publisher omitted arrives as <see cref="Guid.Empty"/>, because the contract
    /// type is the parser's output and its properties are not nullable. Rendering that would write
    /// an all-zeros identifier to the log, which reads as a real one: every uncorrelated event would
    /// share it, and a query for the records that truly had none would return nothing. Absent is
    /// reported as absent.
    /// </remarks>
    private static string? Identifier(Guid value) => value == Guid.Empty ? null : value.ToString();

    /// <remarks>
    /// Bounded before it enters the scope, because at this point it is unvalidated publisher text.
    /// The contract caps an order identifier at <see cref="OrderContract.MaxOrderIdLength"/>, but
    /// nothing has checked that yet — a body may carry a quarter of a megabyte of it and still parse.
    /// Logged whole, that single line exceeds the CloudWatch event limit, so the line explaining why
    /// the message was rejected is the one truncated or dropped, on every redelivery until the
    /// dead-letter queue. The first characters are enough to recognise it by; the validator is what
    /// reports the length.
    /// </remarks>
    private static string? OrderIdentifier(string? orderId) =>
        orderId is { Length: > OrderContract.MaxOrderIdLength }
            ? orderId[..OrderContract.MaxOrderIdLength]
            : orderId;
}
