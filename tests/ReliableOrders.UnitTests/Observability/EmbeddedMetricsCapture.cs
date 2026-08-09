using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using ReliableOrders.Aws.Telemetry;

namespace ReliableOrders.UnitTests.Observability;

/// <summary>
/// Collects the Embedded Metric Format records a publisher writes, as parsed JSON.
/// </summary>
/// <remarks>
/// Assertions are made on the bytes rather than on calls to a fake. What CloudWatch builds a metric
/// from is the record's <c>_aws</c> envelope, and a test satisfied by "the right method was called"
/// would pass with a dimension set that puts an order identifier on every series.
/// </remarks>
internal sealed class EmbeddedMetricsCapture : IDisposable
{
    /// <summary>
    /// A fixed instant, so a published timestamp is something a test can state rather than tolerate.
    /// </summary>
    public static readonly DateTimeOffset Now = new(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);

    public const string MetricNamespace = "ReliableOrders";
    public const string Service = "reliable-orders";
    public const string Environment = "test";

    private readonly StringWriter _writer = new();

    public EmbeddedMetricsCapture() =>
        Publisher = new EmbeddedMetricsPublisher(
            _writer,
            new FakeTimeProvider(Now),
            MetricNamespace,
            Service,
            Environment);

    public EmbeddedMetricsPublisher Publisher { get; }

    /// <summary>
    /// Every record written so far. A parse failure is itself the assertion: CloudWatch reads one
    /// line as one record, so a record spanning two lines is not a metric at all.
    /// </summary>
    public IReadOnlyList<JsonElement> Records =>
    [
        .. _writer.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone()),
    ];

    public JsonElement SingleRecord => Assert.Single(Records);

    public void Dispose() => _writer.Dispose();

    /// <summary>
    /// The value of a counter, or zero when the record does not publish it.
    /// </summary>
    /// <remarks>
    /// Absent and zero are the same question for a counter, and keeping them one helper stops a test
    /// asserting a count of zero in a way that would also pass if the metric had been renamed.
    /// <see cref="DeclaresMetric"/> is what distinguishes the two where it matters.
    /// </remarks>
    public static long Count(JsonElement record, string metric) =>
        record.TryGetProperty(metric, out var value) ? value.GetInt64() : 0;

    /// <summary>
    /// Whether the record's <c>_aws</c> envelope declares this metric, which is what makes a property
    /// a metric rather than ordinary metadata.
    /// </summary>
    public static bool DeclaresMetric(JsonElement record, string metric) =>
        DeclaredMetrics(record).Contains(metric, StringComparer.Ordinal);

    public static IReadOnlyList<string> DeclaredMetrics(JsonElement record) =>
    [
        .. Directive(record)
            .GetProperty("Metrics")
            .EnumerateArray()
            .Select(entry => entry.GetProperty("Name").GetString()!),
    ];

    /// <summary>
    /// The single dimension set, flattened.
    /// </summary>
    public static IReadOnlyList<string> Dimensions(JsonElement record) =>
    [
        .. Directive(record)
            .GetProperty("Dimensions")
            .EnumerateArray()
            .SelectMany(set => set.EnumerateArray())
            .Select(name => name.GetString()!),
    ];

    public static IReadOnlyList<long> Latencies(JsonElement record, string metric) =>
        record.TryGetProperty(metric, out var value)
            ? [.. value.EnumerateArray().Select(sample => sample.GetInt64())]
            : [];

    private static JsonElement Directive(JsonElement record) =>
        record.GetProperty("_aws").GetProperty("CloudWatchMetrics").EnumerateArray().Single();
}
