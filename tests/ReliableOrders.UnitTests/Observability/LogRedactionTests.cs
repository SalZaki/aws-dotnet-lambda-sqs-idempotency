using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ReliableOrders.Core.Observability;
using ReliableOrders.Core.Persistence;
using ReliableOrders.Core.Validation;

namespace ReliableOrders.UnitTests.Observability;

/// <summary>
/// Cases 25 and 26 of the plan: no raw message body and no complete DynamoDB item reaches a log line.
/// </summary>
/// <remarks>
/// <para>
/// Two kinds of assertion, and both are needed. Emitting every event and searching the bytes shows
/// that nothing leaks today; checking the shape of the public surface shows which leaks the
/// signatures rule out. A search alone would pass forever after someone added an overload taking an
/// exception, because no test would call it.
/// </para>
/// <para>
/// What the surface check does not cover is a caller passing an exception message as a
/// <c>reason</c>, because a reason is a string. That limit is stated on <see cref="ProcessingLog"/>
/// rather than papered over here: a test named for a guarantee it does not make is worse than no
/// test, because the next person reads the name and stops looking.
/// </para>
/// <para>
/// The body and the item used here are the shapes the real ones take — the fields a publisher sends
/// and the attributes a condition check returns — so a leak shows up as a recognisable value rather
/// than as an opaque string that happens to be absent.
/// </para>
/// </remarks>
public sealed class LogRedactionTests
{
    /// <summary>
    /// The only parameter types the log will accept.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately a list of exact types rather than a rule about what is forbidden. A ban on
    /// <see cref="Exception"/> and on dictionaries is a guess at how the next leak arrives; an
    /// allow-list makes any new parameter type a decision someone has to write down here, which is
    /// where the reasoning about what may be logged belongs.
    /// </para>
    /// <para>
    /// <see cref="string"/> is the weak entry. It is here because a reason and a hash are both text
    /// drawn from a fixed vocabulary, and it is the one type on this list that could carry a payload
    /// if a caller ignored that. Closing it means giving those vocabularies a type of their own, in
    /// Epic 1 and Epic 2 rather than here.
    /// </para>
    /// </remarks>
    private static readonly Type[] PermittedParameterTypes =
    [
        typeof(string),
        typeof(int),
        typeof(long),
        typeof(TimeSpan),
        typeof(DuplicateScope),
        typeof(ConflictScope),
        typeof(ValidationResult),
    ];

    /// <summary>
    /// Nothing the log accepts can carry a payload.
    /// </summary>
    /// <remarks>
    /// This is what makes case 25 and case 26 structural rather than a matter of care at the call
    /// site. A caller holding a message body or a returned item has no method to pass it to.
    /// </remarks>
    [Fact]
    public void The_log_accepts_no_type_that_could_carry_a_body_or_an_item()
    {
        var offenders = typeof(ProcessingLog)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .SelectMany(method => method.GetParameters().Select(parameter => (method, parameter)))
            .Where(entry => !PermittedParameterTypes.Contains(entry.parameter.ParameterType))
            .Select(entry => $"{entry.method.Name}({entry.parameter.ParameterType.Name} {entry.parameter.Name})")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            $"ProcessingLog accepts parameters no redaction rule covers: {string.Join(", ", offenders)}. "
            + "A message body, an exception, or a DynamoDB item reaches CloudWatch through any of "
            + "these. Add the type to PermittedParameterTypes only with a reason it cannot carry a "
            + "payload.");
    }

    /// <summary>
    /// Case 25. A body's contents appear nowhere in what the log writes.
    /// </summary>
    /// <remarks>
    /// Every event is emitted inside a full set of scopes, with the identifiers a real record would
    /// carry, and the whole output is searched. The parse and validation events are the ones that
    /// tempt an implementation to include the offending body, so both are here.
    /// </remarks>
    [Fact]
    public void No_event_writes_any_part_of_a_message_body()
    {
        var written = EmitEveryEvent();

        foreach (var fragment in BodyFragments)
        {
            Assert.DoesNotContain(fragment, written, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Case 26. A returned DynamoDB item's contents appear nowhere in what the log writes.
    /// </summary>
    /// <remarks>
    /// The item exists only inside the classifier, which reads the one hash attribute it compares and
    /// discards the rest. What survives to the conflict event is a scope, a fixed reason, and the hash
    /// this event computed, so the customer and address attributes stored alongside it have no route
    /// to a log line.
    /// </remarks>
    [Fact]
    public void No_event_writes_any_part_of_a_returned_item()
    {
        var written = EmitEveryEvent();

        foreach (var fragment in ItemFragments)
        {
            Assert.DoesNotContain(fragment, written, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// A conflict line carries the hash and nothing beyond the fields it is meant to.
    /// </summary>
    /// <remarks>
    /// The strongest form of case 26 available: rather than searching for values that must be absent,
    /// this pins the complete field set of the line that a condition-check failure produces. Anything
    /// added to it, from any layer, fails here.
    /// </remarks>
    [Fact]
    public void A_conflict_line_carries_exactly_the_fields_it_should()
    {
        using var capture = new JsonLogCapture();
        using var factory = JsonLogCapture.FactoryFor(capture);

        var log = new ProcessingLog(factory.CreateLogger<ProcessingLog>(), Service, Environment);

        using (log.BeginInvocation(LambdaRequestId))
        using (log.BeginRecord(SqsMessageId, 2))
        using (log.BeginOrderIdentity(EventId, OrderId, CorrelationId))
        {
            log.IdempotencyConflict(
                ConflictScope.Order,
                WriteFailureReason.BusinessHashMismatch,
                ComputedHash,
                TimeSpan.FromMilliseconds(12));
        }

        var actual = capture.SingleLine
            .EnumerateObject()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        string[] expected =
        [
            LogFields.ApproximateReceiveCount,
            "Category",
            LogFields.ComputedHash,
            LogFields.CorrelationId,
            LogFields.DurationMs,
            LogFields.Environment,
            LogFields.EventId,
            LogFields.LambdaRequestId,
            "LogEvent",
            "LogEventId",
            "LogLevel",
            "Message",
            LogFields.OrderId,
            LogFields.Outcome,
            LogFields.Reason,
            LogFields.Scope,
            LogFields.Service,
            LogFields.SqsMessageId,
        ];

        Assert.Equal(expected.Order(StringComparer.Ordinal), actual);
    }

    /// <summary>
    /// An exception's message is not written, whoever logged it.
    /// </summary>
    /// <remarks>
    /// <see cref="ProcessingLog"/> takes no exception, but the formatter serves every logger in the
    /// process, and an AWS SDK exception message is where an item's contents or a request body
    /// arrives from outside this service's own statements. The type and the throwing frame identify
    /// the defect without carrying either.
    /// </remarks>
    [Fact]
    public void An_exception_contributes_its_type_and_stack_but_not_its_message()
    {
        using var capture = new JsonLogCapture();
        using var factory = JsonLogCapture.FactoryFor(capture);

        var logger = factory.CreateLogger("Amazon.DynamoDBv2");
        var thrown = Caught();

#pragma warning disable CA1848 // A raw call is the point: this is what a third-party logger does.
        logger.LogError(thrown, "Transaction failed");
#pragma warning restore CA1848

        var line = capture.SingleLine;

        Assert.Equal(typeof(InvalidOperationException).FullName, line.GetProperty("ExceptionType").GetString());
        Assert.Contains(nameof(Caught), line.GetProperty("ExceptionStackTrace").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain(SecretInExceptionMessage, Raw(line), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The template is not written a second time alongside the message.
    /// </summary>
    /// <remarks>
    /// Per-record log volume is the dominant cost of this project, and <c>{OriginalFormat}</c> repeats
    /// what <c>Message</c> already says with its values substituted.
    /// </remarks>
    [Fact]
    public void The_original_format_is_not_written()
    {
        using var capture = new JsonLogCapture();
        using var factory = JsonLogCapture.FactoryFor(capture);

        new ProcessingLog(factory.CreateLogger<ProcessingLog>(), Service, Environment)
            .OrderCreated(TimeSpan.FromMilliseconds(3));

        Assert.False(capture.SingleLine.TryGetProperty("{OriginalFormat}", out _));
    }

    private const string Service = "reliable-orders";
    private const string Environment = "test";
    private const string LambdaRequestId = "8f0a5d5e-1f2b-4c6d-9f5a-2b7c8d9e0f11";
    private const string SqsMessageId = "3a1c9a02-6f28-4a1a-9d3b-1f9f6c2b7e44";
    private const string EventId = "0f6b7c8d-9e0f-4a1b-8c2d-3e4f5a6b7c8d";
    private const string OrderId = "ORD-000123";
    private const string CorrelationId = "corr-77c9";
    private const string ComputedHash = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";
    private const string SecretInExceptionMessage = "4111111111111111";

    /// <summary>
    /// Values a real message body carries, none of which may appear in a log line.
    /// </summary>
    private static readonly string[] BodyFragments =
    [
        "ada.lovelace@example.com",
        "Ada Lovelace",
        "12 Marylebone Road",
        "\"amount\":149.99",
        "schemaVersion",
    ];

    /// <summary>
    /// Attributes a condition-check failure returns alongside the hash that was compared.
    /// </summary>
    /// <remarks>
    /// Stored attribute names and values only. A bare <c>CustomerId</c> would not do: a validation
    /// failure names the field path <c>data.customerId</c>, which is fixed vocabulary and is meant to
    /// be logged, so the probe would fail on a line that is behaving correctly.
    /// </remarks>
    private static readonly string[] ItemFragments =
    [
        "cust-88213",
        "ShippingAddress",
        "BusinessSha256",
        "EnvelopeSha256",
        "ExpirationEpochSeconds",
    ];

    /// <summary>
    /// Every event the log can emit, rendered as one string, inside a full set of scopes.
    /// </summary>
    /// <remarks>
    /// Whole output rather than event by event: a leak is a leak wherever it surfaces, and searching
    /// once means a newly added event is covered by these tests the moment it is called from here.
    /// The dispatch in <c>ProcessingLogTests</c> fails loudly when an event has no case, which is what
    /// keeps this list complete.
    /// </remarks>
    private static string EmitEveryEvent()
    {
        using var capture = new JsonLogCapture();
        using var factory = JsonLogCapture.FactoryFor(capture);

        var log = new ProcessingLog(factory.CreateLogger<ProcessingLog>(), Service, Environment);
        var duration = TimeSpan.FromMilliseconds(9);

        using (log.BeginInvocation(LambdaRequestId))
        {
            log.BatchStarted(1);

            using (log.BeginRecord(SqsMessageId, 3))
            {
                log.MessageParsingFailed(Core.Contracts.ParseFailureReason.InvalidJson, duration);

                using (log.BeginOrderIdentity(EventId, OrderId, CorrelationId))
                {
                    log.MessageValidationFailed(
                        new ValidationResult(
                        [
                            new ValidationFailure("data.customerId", ValidationRule.Required),
                            new ValidationFailure("data.amount", ValidationRule.NotPositive),
                        ]),
                        duration);

                    log.OrderCreated(duration);
                    log.DuplicateIgnored(DuplicateScope.Order, duration);
                    log.IdempotencyConflict(
                        ConflictScope.Order,
                        WriteFailureReason.BusinessHashMismatch,
                        ComputedHash,
                        duration);
                    log.TransientProcessingFailure(WriteFailureReason.Throttled, duration);
                    log.PermanentProcessingFailure(WriteFailureReason.TableNotFound, duration);
                    log.ProcessingDeadlineReached(duration);
                }
            }

            log.BatchCompleted(1, 1, duration);
        }

        return string.Concat(capture.Lines.Select(Raw));
    }

    private static string Raw(JsonElement line) => line.GetRawText();

    /// <remarks>
    /// Thrown and caught so the exception carries a real stack trace. A constructed exception has a
    /// null one, which would let the formatter's stack-trace branch pass untested.
    /// </remarks>
    private static InvalidOperationException Caught()
    {
        try
        {
            throw new InvalidOperationException(
                $"ConditionalCheckFailed on item with pan {SecretInExceptionMessage}");
        }
        catch (InvalidOperationException exception)
        {
            return exception;
        }
    }
}
