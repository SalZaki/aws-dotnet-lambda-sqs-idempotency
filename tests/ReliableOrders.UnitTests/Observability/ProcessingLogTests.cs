using System.Text.Json;
using Microsoft.Extensions.Logging;
using ReliableOrders.Core.Observability;
using ReliableOrders.Core.Persistence;
using ReliableOrders.Core.Validation;

namespace ReliableOrders.UnitTests.Observability;

/// <summary>
/// What an operator can find a record by, and what each event says happened.
/// </summary>
/// <remarks>
/// Assertions go through <see cref="LogFields"/> rather than repeating the field names as literals.
/// The constants are what the Logging Specification and any saved query agree on, so a rename that
/// forgets a message template fails here instead of leaving a query silently matching nothing.
/// </remarks>
public sealed class ProcessingLogTests
{
    [Fact]
    public void Invocation_scope_carries_service_environment_and_request()
    {
        var line = Emit(log =>
        {
            using var invocation = log.BeginInvocation(LambdaRequestId);
            log.BatchStarted(10);
        });

        Assert.Equal(Service, Text(line, LogFields.Service));
        Assert.Equal(Environment, Text(line, LogFields.Environment));
        Assert.Equal(LambdaRequestId, Text(line, LogFields.LambdaRequestId));
        Assert.Equal(10, Number(line, LogFields.RecordCount));
    }

    /// <summary>
    /// The four identifiers the story requires a record to be findable by.
    /// </summary>
    [Fact]
    public void A_record_is_queryable_by_message_event_order_and_correlation()
    {
        var line = Emit(log =>
        {
            using var invocation = log.BeginInvocation(LambdaRequestId);
            using var record = log.BeginRecord(SqsMessageId, 1);
            using var identity = log.BeginOrderIdentity(EventId, OrderId, CorrelationId);
            log.OrderCreated(TimeSpan.FromMilliseconds(42));
        });

        Assert.Equal(SqsMessageId, Text(line, LogFields.SqsMessageId));
        Assert.Equal(EventId, Text(line, LogFields.EventId));
        Assert.Equal(OrderId, Text(line, LogFields.OrderId));
        Assert.Equal(CorrelationId, Text(line, LogFields.CorrelationId));
    }

    /// <summary>
    /// The domain event identifier keeps the <c>EventId</c> field to itself.
    /// </summary>
    /// <remarks>
    /// Both the publisher's identifier and the log statement's number want this name. Losing the
    /// former to the latter would leave every parsed line reporting a small integer where an operator
    /// searches for a UUID, and nothing would look broken.
    /// </remarks>
    [Fact]
    public void The_log_statement_number_does_not_displace_the_domain_event_id()
    {
        var line = Emit(log =>
        {
            using var invocation = log.BeginInvocation(LambdaRequestId);
            using var record = log.BeginRecord(SqsMessageId, 1);
            using var identity = log.BeginOrderIdentity(EventId, OrderId, CorrelationId);
            log.OrderCreated(TimeSpan.FromMilliseconds(1));
        });

        Assert.Equal(EventId, Text(line, LogFields.EventId));
        Assert.Equal(LogEvents.OrderCreated, Number(line, "LogEventId"));
        Assert.Equal(nameof(ProcessingLog.OrderCreated), Text(line, "LogEvent"));
    }

    /// <summary>
    /// An absent correlation identifier is absent, not empty.
    /// </summary>
    [Fact]
    public void A_missing_correlation_id_writes_no_field()
    {
        var line = Emit(log =>
        {
            using var invocation = log.BeginInvocation(LambdaRequestId);
            using var record = log.BeginRecord(SqsMessageId, 1);
            using var identity = log.BeginOrderIdentity(EventId, OrderId, correlationId: null);
            log.OrderCreated(TimeSpan.Zero);
        });

        Assert.False(line.TryGetProperty(LogFields.CorrelationId, out _));
    }

    [Fact]
    public void The_receive_count_travels_with_every_line_about_a_record()
    {
        var line = Emit(log =>
        {
            using var invocation = log.BeginInvocation(LambdaRequestId);
            using var record = log.BeginRecord(SqsMessageId, 4);
            log.MessageParsingFailed(ReliableOrders.Core.Contracts.ParseFailureReason.InvalidJson, TimeSpan.Zero);
        });

        Assert.Equal(4, Number(line, LogFields.ApproximateReceiveCount));
    }

    [Theory]
    [InlineData(nameof(ProcessingLog.OrderCreated), "Processed")]
    [InlineData(nameof(ProcessingLog.DuplicateIgnored), "Duplicate")]
    [InlineData(nameof(ProcessingLog.MessageParsingFailed), "PermanentFailure")]
    [InlineData(nameof(ProcessingLog.MessageValidationFailed), "PermanentFailure")]
    [InlineData(nameof(ProcessingLog.IdempotencyConflict), "PermanentFailure")]
    [InlineData(nameof(ProcessingLog.TransientProcessingFailure), "TransientFailure")]
    [InlineData(nameof(ProcessingLog.PermanentProcessingFailure), "PermanentFailure")]
    [InlineData(nameof(ProcessingLog.ProcessingDeadlineReached), "DeadlineDeferred")]
    public void Every_terminal_event_names_its_outcome(string eventName, string expectedOutcome)
    {
        var line = Emit(log => EmitTerminalEvent(log, eventName));

        Assert.Equal(expectedOutcome, Text(line, LogFields.Outcome));
        Assert.Equal(eventName, Text(line, "LogEvent"));
    }

    /// <summary>
    /// A permanent fault has its own event, and is not reported as a retryable one.
    /// </summary>
    /// <remarks>
    /// Without it the nearest event is <see cref="ProcessingLog.TransientProcessingFailure"/>, which
    /// would stamp a retryable outcome and event 2005 on something no retry can fix — leaving the
    /// PermanentFaults alarm and the log disagreeing about what happened while an operator waits for a
    /// downstream service to recover from a missing IAM action.
    /// </remarks>
    [Fact]
    public void A_permanent_fault_is_not_logged_as_a_transient_one()
    {
        var line = Emit(log => log.PermanentProcessingFailure(WriteFailureReason.AccessDenied, TimeSpan.Zero));

        Assert.Equal(nameof(ProcessingLog.PermanentProcessingFailure), Text(line, "LogEvent"));
        Assert.Equal(LogEvents.PermanentProcessingFailure, Number(line, "LogEventId"));
        Assert.Equal("Error", Text(line, "LogLevel"));
        Assert.Equal(WriteFailureReason.AccessDenied, Text(line, LogFields.Reason));
    }

    /// <summary>
    /// A deferral reports what was left, not how long nothing took.
    /// </summary>
    /// <remarks>
    /// The one terminal event without a duration. A zero would drag down the latency a query derives
    /// from this field at exactly the moment the handler is under the most pressure.
    /// </remarks>
    [Fact]
    public void A_deadline_deferral_reports_its_overrun_and_no_duration()
    {
        var line = Emit(log => log.ProcessingDeadlineReached(TimeSpan.FromMilliseconds(250)));

        Assert.Equal(250, Number(line, LogFields.OverrunMs));
        Assert.False(line.TryGetProperty(LogFields.DurationMs, out _));
    }

    [Fact]
    public void A_duration_is_reported_in_whole_milliseconds()
    {
        var line = Emit(log => log.OrderCreated(TimeSpan.FromMilliseconds(1234.6)));

        Assert.Equal(1235, Number(line, LogFields.DurationMs));
    }

    /// <summary>
    /// A partial batch failure is a successful invocation, so this line is the only warning it gets.
    /// </summary>
    [Theory]
    [InlineData(0, "Information")]
    [InlineData(1, "Warning")]
    public void Batch_completion_is_a_warning_only_when_records_failed(int failureCount, string expectedLevel)
    {
        var line = Emit(log => log.BatchCompleted(10, failureCount, TimeSpan.FromSeconds(1)));

        Assert.Equal(expectedLevel, Text(line, "LogLevel"));
        Assert.Equal(failureCount, Number(line, LogFields.FailureCount));
        Assert.Equal(10, Number(line, LogFields.RecordCount));
    }

    /// <summary>
    /// A conflict reports the scope that refused it, so the runbook knows which hash diverged.
    /// </summary>
    [Fact]
    public void A_conflict_reports_its_scope_reason_and_computed_hash()
    {
        var line = Emit(log => log.IdempotencyConflict(
            ConflictScope.Order,
            WriteFailureReason.BusinessHashMismatch,
            ComputedHash,
            TimeSpan.FromMilliseconds(7)));

        Assert.Equal("Error", Text(line, "LogLevel"));
        Assert.Equal(nameof(ConflictScope.Order), Text(line, LogFields.Scope));
        Assert.Equal(WriteFailureReason.BusinessHashMismatch, Text(line, LogFields.Reason));
        Assert.Equal(ComputedHash, Text(line, LogFields.ComputedHash));
    }

    /// <summary>
    /// Validation failures are reported as field and rule pairs, in the validator's order.
    /// </summary>
    [Fact]
    public void Validation_failures_are_reported_as_field_and_rule_pairs()
    {
        var result = new ValidationResult(
        [
            new ValidationFailure("data.orderId", ValidationRule.Required),
            new ValidationFailure("data.currency", ValidationRule.NotACurrencyCode),
        ]);

        var line = Emit(log => log.MessageValidationFailed(result, TimeSpan.Zero));

        Assert.Equal(
            $"data.orderId:{ValidationRule.Required},data.currency:{ValidationRule.NotACurrencyCode}",
            Text(line, LogFields.FailedRules));
    }

    /// <summary>
    /// Every event carries a distinct, stable identifier.
    /// </summary>
    /// <remarks>
    /// Reused numbers are the failure this guards. Operators filter on them and an alarm built on a
    /// number that later means something else fires on the wrong event, which is the kind of defect
    /// that surfaces during an incident rather than before one.
    /// </remarks>
    [Fact]
    public void Log_event_identifiers_are_unique()
    {
        var identifiers = typeof(LogEvents)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Select(field => (int)field.GetRawConstantValue()!)
            .ToArray();

        Assert.NotEmpty(identifiers);
        Assert.Equal(identifiers.Length, identifiers.Distinct().Count());
    }

    private const string Service = "reliable-orders";
    private const string Environment = "test";
    private const string LambdaRequestId = "8f0a5d5e-1f2b-4c6d-9f5a-2b7c8d9e0f11";
    private const string SqsMessageId = "3a1c9a02-6f28-4a1a-9d3b-1f9f6c2b7e44";
    private const string EventId = "0f6b7c8d-9e0f-4a1b-8c2d-3e4f5a6b7c8d";
    private const string OrderId = "ORD-000123";
    private const string CorrelationId = "corr-77c9";
    private const string ComputedHash = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";

    /// <remarks>
    /// One event per capture. Every assertion here is about a single line's contents, and a helper
    /// that returned a list would let a test pass while asserting on the wrong one.
    /// </remarks>
    private static JsonElement Emit(Action<ProcessingLog> emit)
    {
        using var capture = new JsonLogCapture();
        using var factory = JsonLogCapture.FactoryFor(capture);

        emit(new ProcessingLog(factory.CreateLogger<ProcessingLog>(), Service, Environment));

        return capture.SingleLine;
    }

    /// <remarks>
    /// Dispatch by name so the outcome theory names its cases the way an operator reads them, and so
    /// an event added without an outcome shows up as an unhandled name rather than as no test at all.
    /// </remarks>
    private static void EmitTerminalEvent(ProcessingLog log, string eventName)
    {
        var duration = TimeSpan.FromMilliseconds(5);

        switch (eventName)
        {
            case nameof(ProcessingLog.OrderCreated):
                log.OrderCreated(duration);
                break;
            case nameof(ProcessingLog.DuplicateIgnored):
                log.DuplicateIgnored(DuplicateScope.Event, duration);
                break;
            case nameof(ProcessingLog.MessageParsingFailed):
                log.MessageParsingFailed(ReliableOrders.Core.Contracts.ParseFailureReason.InvalidJson, duration);
                break;
            case nameof(ProcessingLog.MessageValidationFailed):
                log.MessageValidationFailed(
                    new ValidationResult([new ValidationFailure("data.orderId", ValidationRule.Required)]),
                    duration);
                break;
            case nameof(ProcessingLog.IdempotencyConflict):
                log.IdempotencyConflict(
                    ConflictScope.Event,
                    WriteFailureReason.EnvelopeHashMismatch,
                    ComputedHash,
                    duration);
                break;
            case nameof(ProcessingLog.TransientProcessingFailure):
                log.TransientProcessingFailure(WriteFailureReason.Throttled, duration);
                break;
            case nameof(ProcessingLog.PermanentProcessingFailure):
                log.PermanentProcessingFailure(WriteFailureReason.TableNotFound, duration);
                break;
            case nameof(ProcessingLog.ProcessingDeadlineReached):
                log.ProcessingDeadlineReached(duration);
                break;
            default:
                Assert.Fail($"{eventName} has no case here. Add one alongside the event.");
                break;
        }
    }

    private static string Text(JsonElement line, string field) => line.GetProperty(field).GetString()!;

    private static long Number(JsonElement line, string field) => line.GetProperty(field).GetInt64();
}
