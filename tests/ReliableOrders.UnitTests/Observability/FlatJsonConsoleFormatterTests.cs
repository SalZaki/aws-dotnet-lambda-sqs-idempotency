using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using ReliableOrders.Core.Observability;

namespace ReliableOrders.UnitTests.Observability;

/// <summary>
/// The shape of a log line, which is what every saved query and alarm is written against.
/// </summary>
public sealed class FlatJsonConsoleFormatterTests
{
    /// <summary>
    /// A field sits at the same path however many scopes happen to be open.
    /// </summary>
    /// <remarks>
    /// The reason this formatter exists. The framework's JSON formatter nests scopes in an array, so
    /// the SQS message identifier lands at <c>Scopes.1.SqsMessageId</c> on a parse failure and at
    /// <c>Scopes.1.SqsMessageId</c> or <c>Scopes.2.SqsMessageId</c> elsewhere depending on whether the
    /// body parsed. One query cannot match both, and "logs are queryable by SQS message ID" is then
    /// only true of some of the lines.
    /// </remarks>
    [Fact]
    public void A_field_keeps_its_path_whatever_the_scope_depth()
    {
        using var capture = new JsonLogCapture();
        using var factory = JsonLogCapture.FactoryFor(capture);

        var log = new ProcessingLog(factory.CreateLogger<ProcessingLog>(), Service, Environment);

        using (log.BeginInvocation(LambdaRequestId))
        using (log.BeginRecord(SqsMessageId, 1))
        {
            // Two scopes deep: the body never parsed, so there is no identity scope.
            log.MessageParsingFailed(Core.Contracts.ParseFailureReason.EmptyBody, TimeSpan.Zero);

            // Three scopes deep, same record.
            using (log.BeginOrderIdentity(EventId, OrderId, null))
            {
                log.OrderCreated(TimeSpan.Zero);
            }
        }

        Assert.All(
            capture.Lines,
            line => Assert.Equal(SqsMessageId, line.GetProperty(LogFields.SqsMessageId).GetString()));
    }

    /// <summary>
    /// The nearest writer to the event wins.
    /// </summary>
    [Fact]
    public void An_inner_scope_overrides_an_outer_one()
    {
        using var capture = new JsonLogCapture();
        using var factory = JsonLogCapture.FactoryFor(capture);

        var logger = factory.CreateLogger("Test");

        using (logger.BeginScope(new Dictionary<string, object> { ["Depth"] = "outer" }))
        using (logger.BeginScope(new Dictionary<string, object> { ["Depth"] = "inner" }))
        {
            Emit(logger);
        }

        Assert.Equal("inner", capture.SingleLine.GetProperty("Depth").GetString());
    }

    /// <summary>
    /// A record's own fields win over a scope of the same name.
    /// </summary>
    [Fact]
    public void A_records_own_field_overrides_a_scope()
    {
        using var capture = new JsonLogCapture();
        using var factory = JsonLogCapture.FactoryFor(capture);

        var logger = factory.CreateLogger("Test");

        using (logger.BeginScope(new Dictionary<string, object> { ["Depth"] = "scope" }))
        {
            logger.LogInformation("Depth is {Depth}", "state");
        }

        Assert.Equal("state", capture.SingleLine.GetProperty("Depth").GetString());
    }

    /// <summary>
    /// A scope that is not a set of named values contributes nothing.
    /// </summary>
    /// <remarks>
    /// Third-party libraries open scopes over a plain string. There is no field name to query it by,
    /// so writing it under an invented one would put an unmatchable value on every line inside that
    /// scope, at the per-record cost that makes CloudWatch ingestion the dominant expense here.
    /// </remarks>
    [Fact]
    public void A_scope_without_named_values_is_dropped()
    {
        using var capture = new JsonLogCapture();
        using var factory = JsonLogCapture.FactoryFor(capture);

        var logger = factory.CreateLogger("Test");

        using (logger.BeginScope("an unnamed scope"))
        {
            Emit(logger);
        }

        Assert.DoesNotContain("an unnamed scope", capture.SingleLine.GetRawText(), StringComparison.Ordinal);
    }

    /// <summary>
    /// One record is one line, whatever the message contains.
    /// </summary>
    /// <remarks>
    /// CloudWatch treats a newline as a record boundary. A message carrying one would arrive as two
    /// records, of which the second is not JSON, so it is dropped from every query silently.
    /// </remarks>
    [Fact]
    public void A_message_containing_a_newline_stays_on_one_line()
    {
        using var capture = new JsonLogCapture();
        using var factory = JsonLogCapture.FactoryFor(capture);

        var logger = factory.CreateLogger("Test");

        logger.LogInformation("Value is {Value}", "first\nsecond");

        Assert.Equal("first\nsecond", capture.SingleLine.GetProperty("Value").GetString());
    }

    /// <summary>
    /// A configured timestamp is written; the default of none is respected.
    /// </summary>
    /// <remarks>
    /// CloudWatch stamps its own ingestion time on every record, which is when the line arrived rather
    /// than when the work happened. Under a cold start or a full batch those differ by enough to
    /// matter when reconstructing an incident, so the service configures one.
    /// </remarks>
    [Theory]
    [InlineData(null, false)]
    [InlineData("yyyy-MM-ddTHH:mm:ss.fffZ", true)]
    public void A_timestamp_is_written_only_when_one_is_configured(string? format, bool expected)
    {
        using var capture = new JsonLogCapture(new ConsoleFormatterOptions
        {
            IncludeScopes = true,
            UseUtcTimestamp = true,
            TimestampFormat = format,
        });

        using var factory = JsonLogCapture.FactoryFor(capture);

        Emit(factory.CreateLogger("Test"));

        Assert.Equal(expected, capture.SingleLine.TryGetProperty("Timestamp", out _));
    }

    /// <summary>
    /// A field whose name the formatter has already used is moved aside, not written twice.
    /// </summary>
    /// <remarks>
    /// This formatter serves every logger in the process. A third-party template using
    /// <c>{Message}</c> would otherwise produce a line with two properties of that name, which
    /// Utf8JsonWriter does not reject and which no reader resolves the same way.
    /// </remarks>
    [Fact]
    public void A_field_colliding_with_a_reserved_name_is_written_under_a_qualified_one()
    {
        using var capture = new JsonLogCapture();
        using var factory = JsonLogCapture.FactoryFor(capture);

        var logger = factory.CreateLogger("Test");

        logger.LogInformation("Something about {Message} and {Category}", "a value", "another");

        var line = capture.SingleLine;

        Assert.Equal("Something about a value and another", line.GetProperty("Message").GetString());
        Assert.Equal("Test", line.GetProperty("Category").GetString());
        Assert.Equal("a value", line.GetProperty("Field_Message").GetString());
        Assert.Equal("another", line.GetProperty("Field_Category").GetString());
    }

    /// <summary>
    /// A scope opened over a template does not reattach the template to every line inside it.
    /// </summary>
    /// <remarks>
    /// The state path already drops <c>{OriginalFormat}</c>. A scope pushes the same key through a
    /// different route, and per-record log volume is the dominant cost of this project, so the leak
    /// would be paid on every line written inside such a scope.
    /// </remarks>
    [Fact]
    public void A_scope_opened_over_a_template_does_not_write_the_template()
    {
        using var capture = new JsonLogCapture();
        using var factory = JsonLogCapture.FactoryFor(capture);

        var logger = factory.CreateLogger("Test");

        using (logger.BeginScope("processing {Thing}", "an order"))
        {
            Emit(logger);
        }

        var line = capture.SingleLine;

        Assert.Equal("an order", line.GetProperty("Thing").GetString());
        Assert.False(line.TryGetProperty("{OriginalFormat}", out _));
    }

    /// <summary>
    /// A value JSON cannot represent does not take the record down with it.
    /// </summary>
    /// <remarks>
    /// Utf8JsonWriter throws on NaN and on an infinity, and the formatter runs synchronously inside
    /// <see cref="ILogger.Log"/> — so without the guard a log call would fail the record it was
    /// describing. A rate computed over an empty batch is enough to produce one.
    /// </remarks>
    [Theory]
    [InlineData(double.NaN, "NaN")]
    [InlineData(double.PositiveInfinity, "Infinity")]
    [InlineData(double.NegativeInfinity, "-Infinity")]
    public void A_non_finite_number_is_written_as_text_rather_than_throwing(double value, string expected)
    {
        using var capture = new JsonLogCapture();
        using var factory = JsonLogCapture.FactoryFor(capture);

        var logger = factory.CreateLogger("Test");

        logger.LogInformation("Rate is {Rate}", value);

        Assert.Equal(expected, capture.SingleLine.GetProperty("Rate").GetString());
    }

    /// <summary>
    /// A finite double keeps its JSON type, so Logs Insights can compare and average it.
    /// </summary>
    [Fact]
    public void A_finite_number_stays_a_number()
    {
        using var capture = new JsonLogCapture();
        using var factory = JsonLogCapture.FactoryFor(capture);

        var logger = factory.CreateLogger("Test");

        logger.LogInformation("Rate is {Rate}", 0.25d);

        Assert.Equal(0.25d, capture.SingleLine.GetProperty("Rate").GetDouble());
    }

    /// <summary>
    /// Turning scopes off turns them off.
    /// </summary>
    /// <remarks>
    /// The option is set in <c>LoggingConfiguration</c> and reads as a switch. One that silently did
    /// nothing would be worse than not offering it, because the first person to reach for it would
    /// believe they had reduced what the service writes.
    /// </remarks>
    [Fact]
    public void Scopes_are_omitted_when_the_options_say_so()
    {
        using var capture = new JsonLogCapture(new ConsoleFormatterOptions { IncludeScopes = false });
        using var factory = JsonLogCapture.FactoryFor(capture);

        var logger = factory.CreateLogger("Test");

        using (logger.BeginScope(new Dictionary<string, object> { ["Depth"] = "outer" }))
        {
            Emit(logger);
        }

        Assert.False(capture.SingleLine.TryGetProperty("Depth", out _));
    }

    /// <summary>
    /// A formatter failure drops its line and says so, rather than reaching the caller.
    /// </summary>
    /// <remarks>
    /// Formatting runs on the caller's thread here. Without this, a state value whose
    /// <c>ToString</c> throws would leave the log call throwing into record processing, and a handler
    /// would return an order it had already committed as a batch item failure.
    /// </remarks>
    [Fact]
    public void A_value_that_throws_while_formatting_does_not_reach_the_caller()
    {
        using var capture = new JsonLogCapture();
        using var factory = JsonLogCapture.FactoryFor(capture);

        var logger = factory.CreateLogger("Test");

        logger.LogInformation("Value is {Value}", new ThrowsWhenFormatted());

        var line = capture.SingleLine;

        Assert.Equal("Error", line.GetProperty("LogLevel").GetString());
        Assert.Contains("dropped", line.GetProperty("Message").GetString()!, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every numeric type stays a number, not just the four with a direct writer overload.
    /// </summary>
    /// <remarks>
    /// A missed type arrives as a quoted string, and a Logs Insights query that averages it returns no
    /// rows rather than an error — the silent empty result this formatter exists to avoid.
    /// </remarks>
    [Theory]
    [InlineData((int)12)]
    [InlineData((long)12)]
    [InlineData((uint)12)]
    [InlineData((ulong)12)]
    [InlineData((short)12)]
    [InlineData((ushort)12)]
    [InlineData((byte)12)]
    [InlineData((sbyte)12)]
    [InlineData(12.0f)]
    [InlineData(12.0d)]
    public void Every_numeric_type_keeps_its_json_type(object value)
    {
        using var capture = new JsonLogCapture();
        using var factory = JsonLogCapture.FactoryFor(capture);

        var logger = factory.CreateLogger("Test");

        logger.LogInformation("Depth is {Depth}", value);

        Assert.Equal(12, capture.SingleLine.GetProperty("Depth").GetDouble());
    }

    /// <summary>
    /// A name the record did not write is left alone.
    /// </summary>
    /// <remarks>
    /// <c>Timestamp</c> is only written when a format is configured. Reserving it regardless would
    /// move a field genuinely called <c>Timestamp</c> aside while leaving the name free, putting the
    /// value at a path no query written against the production shape would match.
    /// </remarks>
    [Fact]
    public void A_reserved_name_the_record_did_not_write_is_not_taken()
    {
        using var capture = new JsonLogCapture(new ConsoleFormatterOptions { IncludeScopes = true });
        using var factory = JsonLogCapture.FactoryFor(capture);

        var logger = factory.CreateLogger("Test");

        logger.LogInformation("A line with {Timestamp}", "a value of its own");

        var line = capture.SingleLine;

        Assert.Equal("a value of its own", line.GetProperty("Timestamp").GetString());
        Assert.False(line.TryGetProperty("Field_Timestamp", out _));
    }

    /// <summary>
    /// Stands in for a state value that fails while being written.
    /// </summary>
    private sealed class ThrowsWhenFormatted
    {
        public override string ToString() => throw new InvalidOperationException("cannot render");
    }

    private const string Service = "reliable-orders";
    private const string Environment = "test";
    private const string LambdaRequestId = "8f0a5d5e-1f2b-4c6d-9f5a-2b7c8d9e0f11";
    private const string SqsMessageId = "3a1c9a02-6f28-4a1a-9d3b-1f9f6c2b7e44";
    private const string EventId = "0f6b7c8d-9e0f-4a1b-8c2d-3e4f5a6b7c8d";
    private const string OrderId = "ORD-000123";

    private static void Emit(ILogger logger)
    {
        logger.LogInformation("A line");
    }
}
