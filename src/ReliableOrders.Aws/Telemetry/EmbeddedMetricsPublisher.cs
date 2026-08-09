using System.Buffers;
using System.Text;
using System.Text.Json;
using ReliableOrders.Core.Observability;

namespace ReliableOrders.Aws.Telemetry;

/// <summary>
/// Publishes an invocation's metrics as one CloudWatch Embedded Metric Format record on stdout.
/// </summary>
/// <remarks>
/// <para>
/// Standard output is the whole transport, as it is for logs. Nothing here calls
/// <c>PutMetricData</c>: a synchronous CloudWatch call on the record path would add its own latency
/// and its own failure mode to work that is already being measured against a deadline, and it is the
/// reason the Metrics Specification asks for EMF rather than the API.
/// </para>
/// <para>
/// One record per invocation, not per message. Per-record EMF is what makes CloudWatch Logs
/// ingestion the dominant cost of this project at any real volume, and EMF's support for an array of
/// values against a single metric exists for exactly this: ten records become one line carrying ten
/// latencies, and CloudWatch derives the same statistics from it. The cost of aggregating is that
/// metrics exist only in memory until the invocation ends, which is why publishing happens on
/// disposal rather than on an explicit call — an invocation that throws still reports what it
/// managed, and the outcomes most worth counting are the ones nearest a failure.
/// </para>
/// <para>
/// This type is created once per execution environment and holds no per-invocation state. The
/// accumulator that does is <see cref="BeginInvocation"/>'s return value.
/// </para>
/// </remarks>
public sealed class EmbeddedMetricsPublisher
{
    private readonly TextWriter _output;
    private readonly TimeProvider _clock;
    private readonly string _metricNamespace;
    private readonly string _service;
    private readonly string _environment;

    /// <summary>
    /// Creates a publisher for one execution environment.
    /// </summary>
    /// <param name="output">Where records are written. Standard output in the function.</param>
    /// <param name="clock">
    /// Supplies the record timestamp. Injected rather than read from <c>DateTimeOffset.UtcNow</c>
    /// because a test asserting on a published record has to know what the timestamp will be, and
    /// because this is telemetry rather than a value written inside a transaction — the determinism
    /// rule that keeps a clock out of <c>OrderWriteRequest</c> does not apply here.
    /// </param>
    /// <param name="metricNamespace">The CloudWatch namespace every metric is published under.</param>
    /// <param name="service">The service dimension's value.</param>
    /// <param name="environment">The environment dimension's value.</param>
    public EmbeddedMetricsPublisher(
        TextWriter output,
        TimeProvider clock,
        string metricNamespace,
        string service,
        string environment)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentException.ThrowIfNullOrWhiteSpace(metricNamespace);
        ArgumentException.ThrowIfNullOrWhiteSpace(service);
        ArgumentException.ThrowIfNullOrWhiteSpace(environment);

        _output = output;
        _clock = clock;
        _metricNamespace = metricNamespace;
        _service = service;
        _environment = environment;
    }

    /// <summary>
    /// Starts collecting one invocation's metrics.
    /// </summary>
    /// <param name="recordCount">How many records the invocation received.</param>
    /// <returns>The accumulator, which publishes when disposed.</returns>
    public IInvocationMetrics BeginInvocation(int recordCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(recordCount);

        return new InvocationMetrics(this, recordCount);
    }

    /// <summary>
    /// EMF permits at most this many values against one metric in one record.
    /// </summary>
    /// <remarks>
    /// Unreachable at the configured batch size of ten, and guarded anyway. The batch size is a CDK
    /// parameter, and a service that started dropping latency samples because someone raised it would
    /// show no error — only a distribution that had quietly stopped describing the whole batch.
    /// </remarks>
    private const int MaxValuesPerMetric = 100;

    /// <summary>
    /// The outcome counters published on every invocation, at zero when they did not occur.
    /// </summary>
    /// <remarks>
    /// Required alarm 7 is a composite over <c>ApproximateNumberOfMessagesVisible</c> and the sum of
    /// these two, and it fires when messages are available and neither is moving. Omitting them when
    /// they are zero would leave that sum with no datapoints during exactly the outage it watches for,
    /// so the alarm would report insufficient data instead of firing. The sum is what matters rather
    /// than new orders alone, because a replay storm is processed correctly while
    /// <see cref="MetricNames.OrdersProcessed"/> stays flat.
    /// </remarks>
    private static readonly string[] ContinuousCounters =
        [MetricNames.OrdersProcessed, MetricNames.DuplicateEvents];

    private const string CountUnit = "Count";
    private const string MillisecondsUnit = "Milliseconds";

    /// <remarks>
    /// The dimension set, and the whole of it. Every other value in the record is an ordinary property
    /// that CloudWatch does not build a series from, which is what keeps
    /// <c>OrderId</c>, <c>EventId</c> and <c>SqsMessageId</c> out of the metric's cardinality even if
    /// one is ever added to a record for correlation.
    /// </remarks>
    private static readonly string[] DimensionNames = [LogFields.Service, LogFields.Environment];

    private static readonly JsonWriterOptions WriterOptions = new() { Indented = false, SkipValidation = false };

    /// <summary>
    /// One invocation's counts and latencies, published on disposal.
    /// </summary>
    /// <remarks>
    /// Guarded by a lock. Records are processed sequentially today, and the batch handler is expected
    /// to gain bounded parallelism once correctness tests and metrics exist — which is this story. An
    /// accumulator that lost increments under that change would misreport quietly rather than fail,
    /// and the cost of preventing it is one uncontended lock per record.
    /// </remarks>
    private sealed class InvocationMetrics(EmbeddedMetricsPublisher publisher, int recordCount) : IInvocationMetrics
    {
        private readonly Lock _gate = new();
        private readonly List<long> _latencies = [];
        private readonly Dictionary<string, long> _counts = new(StringComparer.Ordinal);
        private int _failures;
        private bool _published;

        public void OrderProcessed(TimeSpan duration) => Record(MetricNames.OrdersProcessed, duration);

        public void DuplicateEvent(TimeSpan duration) => Record(MetricNames.DuplicateEvents, duration);

        public void InvalidEvent(int approximateReceiveCount, TimeSpan duration) =>
            RecordPermanent(MetricNames.ValidationFailures, approximateReceiveCount, duration);

        public void IdempotencyConflict(int approximateReceiveCount, TimeSpan duration) =>
            RecordPermanent(MetricNames.IdempotencyConflicts, approximateReceiveCount, duration);

        public void PermanentFault(int approximateReceiveCount, TimeSpan duration) =>
            RecordPermanent(MetricNames.PermanentFaults, approximateReceiveCount, duration);

        public void TransientFailure(TimeSpan duration)
        {
            lock (_gate)
            {
                Increment(MetricNames.TransientFailures);
                _latencies.Add(Milliseconds(duration));
                _failures++;
            }
        }

        public void DeadlineDeferral()
        {
            lock (_gate)
            {
                Increment(MetricNames.DeadlineDeferrals);
                _failures++;
            }
        }

        /// <remarks>
        /// Publishing writes and flushes, so it can fail — a broken pipe on a sandbox being reclaimed
        /// is enough. Disposal usually runs from a <c>using</c>, and an exception thrown there while
        /// the block is already unwinding replaces the exception that caused the unwinding: the
        /// failure an operator needs would be lost and an I/O error reported in its place. A failed
        /// publish costs one invocation's metrics, which is the smaller loss by a wide margin.
        /// </remarks>
        public void Dispose()
        {
            lock (_gate)
            {
                // Publishing twice would double every count. Disposal is idempotent rather than
                // guarded at the call site because the handler is expected to dispose inside a using
                // and may also dispose on an error path. The flag is set before the write, not after,
                // so a write that failed halfway is not repeated on a second disposal.
                if (_published)
                {
                    return;
                }

                _published = true;

                try
                {
                    publisher.Publish(recordCount, _failures, _counts, _latencies);
                }
#pragma warning disable CA1031 // Nothing a write throws may escape a Dispose.
                catch (Exception)
#pragma warning restore CA1031
                {
                }
            }
        }

        /// <remarks>
        /// The gate from the Retry Amplification of Permanent Failures section, applied in the one
        /// place all three permanent outcomes pass through. What is suppressed is the outcome's own
        /// counter and nothing else: the record is still returned as a batch item failure on every
        /// delivery, so it still counts towards <see cref="MetricNames.BatchFailures"/>, and the work
        /// still took time, so its latency is still sampled. Suppressing those as well would make a
        /// queue full of poison messages look idle.
        /// </remarks>
        private void RecordPermanent(string metric, int approximateReceiveCount, TimeSpan duration)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(approximateReceiveCount, 1);

            lock (_gate)
            {
                if (approximateReceiveCount == 1)
                {
                    Increment(metric);
                }

                _latencies.Add(Milliseconds(duration));
                _failures++;
            }
        }

        private void Record(string metric, TimeSpan duration)
        {
            lock (_gate)
            {
                Increment(metric);
                _latencies.Add(Milliseconds(duration));
            }
        }

        private void Increment(string metric) =>
            _counts[metric] = _counts.TryGetValue(metric, out var current) ? current + 1 : 1;

        /// <remarks>
        /// Whole milliseconds, matching the log's <c>DurationMs</c> so the same work reads the same in
        /// both places.
        /// </remarks>
        private static long Milliseconds(TimeSpan duration) => (long)Math.Round(duration.TotalMilliseconds);
    }

    /// <remarks>
    /// <para>
    /// A counter that stayed at zero is omitted rather than published as a zero. The acceptance
    /// criterion for the gate is that one poison message produces exactly one validation-failure data
    /// point across all five deliveries, and publishing zeros on the other four would satisfy the
    /// gate while producing five data points, one of which happens to be zero. Omission makes the
    /// criterion literally true and costs nothing an alarm needs — every threshold in the
    /// specification is "greater than" some value.
    /// </para>
    /// <para>
    /// Four metrics are exempt and are published even at zero. <see cref="MetricNames.BatchSize"/> and
    /// <see cref="MetricNames.BatchFailures"/> describe the invocation rather than an outcome within
    /// it, and a continuous failure series is what makes a partial batch failure visible against a run
    /// of successful invocations. <see cref="ContinuousCounters"/> holds the other two. None of the
    /// four is gated, so exempting them costs the first-receipt guarantee nothing.
    /// </para>
    /// </remarks>
    private void Publish(
        int recordCount,
        int failures,
        Dictionary<string, long> counts,
        List<long> latencies)
    {
        var timestamp = _clock.GetUtcNow().ToUnixTimeMilliseconds();

        foreach (var metric in ContinuousCounters)
        {
            counts.TryAdd(metric, 0);
        }

        // The first record carries the counters. Any latency values beyond what one EMF record may
        // hold follow in further records, which carry nothing else — splitting the samples keeps the
        // distribution complete, while repeating the counters alongside them would double every count.
        WriteRecord(timestamp, counts, recordCount, failures, latencies.Take(MaxValuesPerMetric));

        for (var offset = MaxValuesPerMetric; offset < latencies.Count; offset += MaxValuesPerMetric)
        {
            WriteRecord(
                timestamp,
                counts: null,
                recordCount: null,
                failures: null,
                latencies.Skip(offset).Take(MaxValuesPerMetric));
        }

        _output.Flush();
    }

    private void WriteRecord(
        long timestamp,
        Dictionary<string, long>? counts,
        int? recordCount,
        int? failures,
        IEnumerable<long> latencies)
    {
        var samples = latencies.ToArray();

        var published = new List<(string Name, string Unit)>();

        if (recordCount is not null)
        {
            published.Add((MetricNames.BatchSize, CountUnit));
        }

        if (failures is not null)
        {
            published.Add((MetricNames.BatchFailures, CountUnit));
        }

        if (counts is not null)
        {
            published.AddRange(counts.Keys.Order(StringComparer.Ordinal).Select(name => (name, CountUnit)));
        }

        if (samples.Length > 0)
        {
            published.Add((MetricNames.RecordProcessingLatency, MillisecondsUnit));
        }

        // An EMF record declaring no metric is a log line pretending to be one. It costs ingestion and
        // CloudWatch derives nothing from it.
        if (published.Count == 0)
        {
            return;
        }

        var buffer = new ArrayBufferWriter<byte>(InitialRecordBytes);

        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();

            WriteMetadata(writer, timestamp, _metricNamespace, published);

            writer.WriteString(LogFields.Service, _service);
            writer.WriteString(LogFields.Environment, _environment);

            if (recordCount is { } size)
            {
                writer.WriteNumber(MetricNames.BatchSize, size);
            }

            if (failures is { } failed)
            {
                writer.WriteNumber(MetricNames.BatchFailures, failed);
            }

            if (counts is not null)
            {
                foreach (var name in counts.Keys.Order(StringComparer.Ordinal))
                {
                    writer.WriteNumber(name, counts[name]);
                }
            }

            if (samples.Length > 0)
            {
                writer.WriteStartArray(MetricNames.RecordProcessingLatency);

                foreach (var sample in samples)
                {
                    writer.WriteNumberValue(sample);
                }

                writer.WriteEndArray();
            }

            writer.WriteEndObject();
            writer.Flush();
        }

        _output.WriteLine(Encoding.UTF8.GetString(buffer.WrittenSpan));
    }

    /// <remarks>
    /// The <c>_aws</c> envelope is what tells CloudWatch this line is a metric record rather than an
    /// ordinary log line. Everything outside it is an ordinary JSON property; a property becomes a
    /// metric only by being named here, and a dimension only by appearing in the dimension set.
    /// </remarks>
    private static void WriteMetadata(
        Utf8JsonWriter writer,
        long timestamp,
        string metricNamespace,
        IReadOnlyList<(string Name, string Unit)> metrics)
    {
        writer.WriteStartObject("_aws");
        writer.WriteNumber("Timestamp", timestamp);
        writer.WriteStartArray("CloudWatchMetrics");
        writer.WriteStartObject();
        writer.WriteString("Namespace", metricNamespace);

        writer.WriteStartArray("Dimensions");
        writer.WriteStartArray();

        foreach (var dimension in DimensionNames)
        {
            writer.WriteStringValue(dimension);
        }

        writer.WriteEndArray();
        writer.WriteEndArray();

        writer.WriteStartArray("Metrics");

        foreach (var (name, unit) in metrics)
        {
            writer.WriteStartObject();
            writer.WriteString("Name", name);
            writer.WriteString("Unit", unit);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    /// <summary>
    /// Sized for an invocation at the configured batch size so the common case writes without growing.
    /// </summary>
    private const int InitialRecordBytes = 2048;
}
