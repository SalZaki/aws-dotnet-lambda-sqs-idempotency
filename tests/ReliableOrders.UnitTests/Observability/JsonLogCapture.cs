using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using ReliableOrders.Function.Observability;

namespace ReliableOrders.UnitTests.Observability;

/// <summary>
/// Collects what the service would actually write, as parsed JSON.
/// </summary>
/// <remarks>
/// <para>
/// The real provider and the real formatter, writing into a <see cref="StringWriter"/> instead of
/// standard output. Asserting on the bytes a formatter produced is the only form of these tests worth
/// having: a redaction claim checked against a recorded call to a mock logger says the argument was
/// not passed, not that the value never reached a log line, and the two differ exactly where a scope
/// or a formatter is what leaks.
/// </para>
/// <para>
/// Standard output is deliberately left alone. Redirecting <c>Console.Out</c> is process-global, and
/// xunit runs test classes in parallel, so a suite built on it would interleave lines from unrelated
/// tests and fail in a way that looks like a formatter bug.
/// </para>
/// </remarks>
internal sealed class JsonLogCapture : IDisposable
{
    private readonly StringWriter _writer = new();
    private readonly SynchronousConsoleLoggerProvider _provider;

    public JsonLogCapture(ConsoleFormatterOptions? options = null) =>
        _provider = new SynchronousConsoleLoggerProvider(
            _writer,
            new FlatJsonConsoleFormatter(options ?? new ConsoleFormatterOptions { IncludeScopes = true }));

    /// <summary>
    /// Every line written so far, parsed. A parse failure here is itself the assertion: CloudWatch
    /// treats a newline as a record boundary, so a line that is not one JSON object is not queryable.
    /// </summary>
    public IReadOnlyList<JsonElement> Lines =>
    [
        .. _writer.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone()),
    ];

    /// <summary>
    /// The only line written, failing with the actual count when there is not exactly one.
    /// </summary>
    public JsonElement SingleLine => Assert.Single(Lines);

    /// <summary>
    /// A factory wired to this capture, with filtering left wide open so a test sees what the
    /// formatter did rather than what a level filter allowed through.
    /// </summary>
    public static ILoggerFactory FactoryFor(JsonLogCapture capture) =>
        LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddProvider(capture._provider);
        });

    public void Dispose()
    {
        _provider.Dispose();
        _writer.Dispose();
    }
}
