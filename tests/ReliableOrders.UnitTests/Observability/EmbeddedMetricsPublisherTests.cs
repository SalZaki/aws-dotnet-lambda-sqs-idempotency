using System.Text.Json;
using ReliableOrders.Core.Observability;

namespace ReliableOrders.UnitTests.Observability;

/// <summary>
/// What one invocation publishes, and the shape CloudWatch reads it as.
/// </summary>
public sealed class EmbeddedMetricsPublisherTests
{
    /// <summary>
    /// Nothing is published until the invocation ends.
    /// </summary>
    /// <remarks>
    /// The aggregation this design exists for. One record per invocation rather than one per message
    /// is what keeps CloudWatch Logs ingestion, the dominant cost of this project, proportional to
    /// invocations instead of to traffic.
    /// </remarks>
    [Fact]
    public void An_invocation_publishes_one_record_covering_every_message()
    {
        using var capture = new EmbeddedMetricsCapture();

        var metrics = capture.Publisher.BeginInvocation(3);

        metrics.OrderProcessed(TimeSpan.FromMilliseconds(10));
        metrics.OrderProcessed(TimeSpan.FromMilliseconds(20));
        metrics.DuplicateEvent(TimeSpan.FromMilliseconds(30));

        Assert.Empty(capture.Records);

        metrics.Dispose();

        var record = capture.SingleRecord;

        Assert.Equal(2, EmbeddedMetricsCapture.Count(record, MetricNames.OrdersProcessed));
        Assert.Equal(1, EmbeddedMetricsCapture.Count(record, MetricNames.DuplicateEvents));
        Assert.Equal(3, EmbeddedMetricsCapture.Count(record, MetricNames.BatchSize));
        Assert.Equal([10, 20, 30], EmbeddedMetricsCapture.Latencies(record, MetricNames.RecordProcessingLatency));
    }

    /// <summary>
    /// Case 27. The dimension set is exactly service and environment.
    /// </summary>
    [Fact]
    public void Dimensions_are_service_and_environment_and_nothing_else()
    {
        var record = Publish(1, metrics => metrics.OrderProcessed(TimeSpan.FromMilliseconds(5)));

        Assert.Equal(
            [LogFields.Service, LogFields.Environment],
            EmbeddedMetricsCapture.Dimensions(record));
    }

    /// <summary>
    /// Case 27, from the other side. No identifier appears anywhere in a published record.
    /// </summary>
    /// <remarks>
    /// A dimension check alone would pass while an order identifier sat in the record as an ordinary
    /// property. That would not create a series, so it would not break CloudWatch, but it would put a
    /// per-record identifier into the metric stream at the per-record cost this design exists to
    /// avoid — and it is one alarm's worth of distance from becoming a dimension. Nothing here takes
    /// an identifier at all, and this holds it that way.
    /// </remarks>
    [Fact]
    public void No_high_cardinality_identifier_reaches_a_record()
    {
        var record = Publish(
            1,
            metrics =>
            {
                metrics.OrderProcessed(TimeSpan.FromMilliseconds(5));
                metrics.IdempotencyConflict(1, TimeSpan.FromMilliseconds(6));
            });

        var raw = record.GetRawText();

        foreach (var forbidden in HighCardinalityFields)
        {
            Assert.DoesNotContain(forbidden, raw, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Case 28, and the story's acceptance criterion. Five deliveries, one data point.
    /// </summary>
    /// <remarks>
    /// Each delivery is its own invocation, because that is how SQS redelivers. The assertion is on
    /// data points rather than on their sum: a zero published on the other four deliveries would make
    /// the sum correct and the criterion false, and it is the count of points that a "greater than
    /// zero" alarm reacts to.
    /// </remarks>
    [Theory]
    [InlineData(nameof(IInvocationMetrics.InvalidEvent), MetricNames.ValidationFailures)]
    [InlineData(nameof(IInvocationMetrics.IdempotencyConflict), MetricNames.IdempotencyConflicts)]
    [InlineData(nameof(IInvocationMetrics.PermanentFault), MetricNames.PermanentFaults)]
    public void One_poison_message_produces_one_data_point_across_five_deliveries(string method, string metric)
    {
        using var capture = new EmbeddedMetricsCapture();

        for (var delivery = 1; delivery <= MaxReceiveCount; delivery++)
        {
            using var metrics = capture.Publisher.BeginInvocation(1);

            RecordPermanent(metrics, method, delivery);
        }

        var points = capture.Records
            .Where(record => EmbeddedMetricsCapture.DeclaresMetric(record, metric))
            .ToArray();

        Assert.Equal(MaxReceiveCount, capture.Records.Count);
        Assert.Single(points);
        Assert.Equal(1, EmbeddedMetricsCapture.Count(points[0], metric));
    }

    /// <summary>
    /// What the gate does not suppress.
    /// </summary>
    /// <remarks>
    /// The record is returned as a batch item failure on every delivery and the work took time on
    /// every delivery. Suppressing either alongside the counter would make a queue full of poison
    /// messages look idle, which is the opposite of what the gate is for.
    /// </remarks>
    [Fact]
    public void A_redelivered_poison_message_still_counts_as_a_failure_and_a_latency_sample()
    {
        var record = Publish(1, metrics => metrics.InvalidEvent(4, TimeSpan.FromMilliseconds(12)));

        Assert.False(EmbeddedMetricsCapture.DeclaresMetric(record, MetricNames.ValidationFailures));
        Assert.Equal(1, EmbeddedMetricsCapture.Count(record, MetricNames.BatchFailures));
        Assert.Equal([12], EmbeddedMetricsCapture.Latencies(record, MetricNames.RecordProcessingLatency));
    }

    /// <summary>
    /// Transient failures are counted on every delivery.
    /// </summary>
    /// <remarks>
    /// The complement of the gate. A downstream fault that recurs across redeliveries is getting
    /// worse, and gating it would hide the only signal that says so.
    /// </remarks>
    [Fact]
    public void A_transient_failure_is_counted_on_every_delivery()
    {
        using var capture = new EmbeddedMetricsCapture();

        for (var delivery = 1; delivery <= MaxReceiveCount; delivery++)
        {
            using var metrics = capture.Publisher.BeginInvocation(1);

            metrics.TransientFailure(TimeSpan.FromMilliseconds(8));
        }

        Assert.All(
            capture.Records,
            record => Assert.Equal(1, EmbeddedMetricsCapture.Count(record, MetricNames.TransientFailures)));
    }

    /// <summary>
    /// A deferral counts as a failure and contributes no latency.
    /// </summary>
    /// <remarks>
    /// The only outcome that records no duration, and the omission is the design rather than an
    /// oversight: no work was done, and a near-zero sample would drag the distribution down at exactly
    /// the moment the handler is under most pressure, which is when that distribution is read. The
    /// batch here mixes a processed record with a deferred one, so the assertion is that the deferral
    /// added nothing rather than that the invocation happened to sample nothing.
    /// </remarks>
    [Fact]
    public void A_deadline_deferral_counts_as_a_failure_and_contributes_no_latency()
    {
        var record = Publish(
            2,
            metrics =>
            {
                metrics.OrderProcessed(TimeSpan.FromMilliseconds(15));
                metrics.DeadlineDeferral();
            });

        Assert.Equal(1, EmbeddedMetricsCapture.Count(record, MetricNames.DeadlineDeferrals));
        Assert.Equal(1, EmbeddedMetricsCapture.Count(record, MetricNames.BatchFailures));
        Assert.Equal([15], EmbeddedMetricsCapture.Latencies(record, MetricNames.RecordProcessingLatency));
    }

    /// <summary>
    /// An invocation that only deferred publishes no latency metric at all.
    /// </summary>
    /// <remarks>
    /// EMF rejects an empty array, so the metric has to be absent rather than present and empty. This
    /// is the case that would produce one if the deferral ever started sampling.
    /// </remarks>
    [Fact]
    public void An_invocation_that_only_deferred_declares_no_latency()
    {
        var record = Publish(1, metrics => metrics.DeadlineDeferral());

        Assert.False(EmbeddedMetricsCapture.DeclaresMetric(record, MetricNames.RecordProcessingLatency));
        Assert.Equal(1, EmbeddedMetricsCapture.Count(record, MetricNames.DeadlineDeferrals));
    }

    /// <summary>
    /// The story's other criterion. A partial failure is visible on an invocation Lambda calls a
    /// success.
    /// </summary>
    [Fact]
    public void Partial_failures_are_published_by_an_invocation_that_succeeded()
    {
        var record = Publish(
            3,
            metrics =>
            {
                metrics.OrderProcessed(TimeSpan.FromMilliseconds(5));
                metrics.OrderProcessed(TimeSpan.FromMilliseconds(6));
                metrics.TransientFailure(TimeSpan.FromMilliseconds(7));
            });

        Assert.Equal(3, EmbeddedMetricsCapture.Count(record, MetricNames.BatchSize));
        Assert.Equal(1, EmbeddedMetricsCapture.Count(record, MetricNames.BatchFailures));
    }

    /// <summary>
    /// Batch size and failures are published even when nothing failed.
    /// </summary>
    /// <remarks>
    /// A continuous failure series is what makes a partial failure legible: a one against a run of
    /// zeros reads as an event, whereas a one against gaps reads as the first data the metric has
    /// ever had.
    /// </remarks>
    [Fact]
    public void A_clean_invocation_still_publishes_batch_size_and_failures()
    {
        var record = Publish(2, metrics =>
        {
            metrics.OrderProcessed(TimeSpan.FromMilliseconds(5));
            metrics.OrderProcessed(TimeSpan.FromMilliseconds(5));
        });

        Assert.True(EmbeddedMetricsCapture.DeclaresMetric(record, MetricNames.BatchFailures));
        Assert.Equal(0, EmbeddedMetricsCapture.Count(record, MetricNames.BatchFailures));
    }

    /// <summary>
    /// A gated counter at zero is not published at all.
    /// </summary>
    /// <remarks>
    /// The four continuous metrics are always there; nothing else is unless it happened. A record from
    /// an invocation that only processed orders therefore declares exactly this set.
    /// </remarks>
    [Fact]
    public void An_outcome_that_did_not_happen_publishes_no_metric()
    {
        var record = Publish(1, metrics => metrics.OrderProcessed(TimeSpan.FromMilliseconds(5)));

        Assert.Equal(
            [
                MetricNames.BatchSize,
                MetricNames.BatchFailures,
                MetricNames.DuplicateEvents,
                MetricNames.OrdersProcessed,
                MetricNames.RecordProcessingLatency,
            ],
            EmbeddedMetricsCapture.DeclaredMetrics(record));

        Assert.False(EmbeddedMetricsCapture.DeclaresMetric(record, MetricNames.ValidationFailures));
        Assert.False(EmbeddedMetricsCapture.DeclaresMetric(record, MetricNames.TransientFailures));
    }

    /// <summary>
    /// The no-progress alarm can still evaluate during an outage.
    /// </summary>
    /// <remarks>
    /// Required alarm 7 is a composite over queue depth and the sum of these two counters, and it
    /// exists for the case where messages are available and neither is moving. If an invocation that
    /// processed nothing published neither, the sum would have no datapoints during exactly that
    /// outage, so the alarm would report insufficient data rather than firing. Both are published at
    /// zero, and neither is gated, so this costs the first-receipt guarantee nothing.
    /// </remarks>
    [Fact]
    public void An_invocation_that_processed_nothing_still_publishes_the_no_progress_counters()
    {
        var record = Publish(
            2,
            metrics =>
            {
                metrics.TransientFailure(TimeSpan.FromMilliseconds(5));
                metrics.TransientFailure(TimeSpan.FromMilliseconds(6));
            });

        Assert.True(EmbeddedMetricsCapture.DeclaresMetric(record, MetricNames.OrdersProcessed));
        Assert.True(EmbeddedMetricsCapture.DeclaresMetric(record, MetricNames.DuplicateEvents));
        Assert.Equal(0, EmbeddedMetricsCapture.Count(record, MetricNames.OrdersProcessed));
        Assert.Equal(0, EmbeddedMetricsCapture.Count(record, MetricNames.DuplicateEvents));
        Assert.Equal(2, EmbeddedMetricsCapture.Count(record, MetricNames.BatchFailures));
    }

    /// <summary>
    /// An invocation that received nothing publishes nothing worth ingesting.
    /// </summary>
    [Fact]
    public void An_empty_invocation_publishes_a_record_describing_the_empty_batch()
    {
        var record = Publish(0, _ => { });

        Assert.Equal(0, EmbeddedMetricsCapture.Count(record, MetricNames.BatchSize));
        Assert.False(EmbeddedMetricsCapture.DeclaresMetric(record, MetricNames.RecordProcessingLatency));
    }

    /// <summary>
    /// Disposing twice does not publish twice.
    /// </summary>
    /// <remarks>
    /// The handler is expected to dispose inside a <c>using</c> and may also dispose on an error
    /// path. A second publish would double every count in it, which would show as a traffic spike
    /// rather than as an error.
    /// </remarks>
    [Fact]
    public void Publishing_is_idempotent()
    {
        using var capture = new EmbeddedMetricsCapture();

        var metrics = capture.Publisher.BeginInvocation(1);

        metrics.OrderProcessed(TimeSpan.FromMilliseconds(5));
        metrics.Dispose();
        metrics.Dispose();

        Assert.Equal(1, EmbeddedMetricsCapture.Count(capture.SingleRecord, MetricNames.OrdersProcessed));
    }

    /// <summary>
    /// More latency samples than one record may hold are split across records, not dropped.
    /// </summary>
    /// <remarks>
    /// Unreachable at the configured batch size of ten. The batch size is a CDK parameter, and a
    /// service that silently stopped sampling part of each batch because someone raised it would
    /// report a latency distribution that no longer described the batch, with nothing to see.
    /// </remarks>
    [Fact]
    public void Latency_samples_beyond_one_records_limit_are_published_in_further_records()
    {
        const int samples = 250;

        using var capture = new EmbeddedMetricsCapture();

        using (var metrics = capture.Publisher.BeginInvocation(samples))
        {
            for (var index = 0; index < samples; index++)
            {
                metrics.OrderProcessed(TimeSpan.FromMilliseconds(index));
            }
        }

        var published = capture.Records
            .SelectMany(record => EmbeddedMetricsCapture.Latencies(record, MetricNames.RecordProcessingLatency))
            .ToArray();

        Assert.Equal(samples, published.Length);
        Assert.Equal(Enumerable.Range(0, samples).Select(value => (long)value), published);

        // The counters ride on the first record only. Repeating them alongside the overflow samples
        // would multiply every count by the number of records the split happened to need.
        Assert.Equal(
            samples,
            capture.Records.Sum(record => EmbeddedMetricsCapture.Count(record, MetricNames.OrdersProcessed)));
    }

    /// <summary>
    /// Every permanent outcome asks for the delivery number.
    /// </summary>
    /// <remarks>
    /// The structural half of case 28. A method recording a permanent failure without it would have
    /// to decide the gate at the call site, which is where it gets forgotten. This fails when one is
    /// added, rather than waiting for an alarm to fire five times for one bad message.
    /// </remarks>
    [Fact]
    public void Every_permanently_failing_outcome_requires_the_delivery_number()
    {
        var missing = typeof(IInvocationMetrics)
            .GetMethods()
            .Where(method => PermanentOutcomes.Contains(method.Name, StringComparer.Ordinal))
            .Where(method => !method.GetParameters().Any(parameter =>
                string.Equals(parameter.Name, "approximateReceiveCount", StringComparison.Ordinal)))
            .Select(method => method.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            missing.Length == 0,
            $"These record a permanent failure without taking the delivery number: {string.Join(", ", missing)}. "
            + "Without it the caller has to apply the first-receipt gate itself, and a maxReceiveCount "
            + "of five means one bad message emits five data points against an alarm thresholded at "
            + "greater than zero.");
    }

    /// <summary>
    /// The envelope names the namespace and the invocation's timestamp.
    /// </summary>
    [Fact]
    public void The_envelope_carries_the_namespace_and_a_timestamp()
    {
        var record = Publish(1, metrics => metrics.OrderProcessed(TimeSpan.FromMilliseconds(5)));

        var directive = record.GetProperty("_aws").GetProperty("CloudWatchMetrics").EnumerateArray().Single();

        Assert.Equal(EmbeddedMetricsCapture.MetricNamespace, directive.GetProperty("Namespace").GetString());
        Assert.Equal(
            EmbeddedMetricsCapture.Now.ToUnixTimeMilliseconds(),
            record.GetProperty("_aws").GetProperty("Timestamp").GetInt64());
    }

    /// <summary>
    /// The maximum deliveries the queue allows before dead-lettering.
    /// </summary>
    private const int MaxReceiveCount = 5;

    /// <summary>
    /// Identifiers that must never reach a metric record, as a dimension or otherwise.
    /// </summary>
    private static readonly string[] HighCardinalityFields =
    [
        LogFields.OrderId,
        LogFields.EventId,
        LogFields.SqsMessageId,
        LogFields.CorrelationId,
        "CustomerId",
    ];

    /// <summary>
    /// The outcomes the first-receipt gate applies to.
    /// </summary>
    private static readonly string[] PermanentOutcomes =
    [
        nameof(IInvocationMetrics.InvalidEvent),
        nameof(IInvocationMetrics.IdempotencyConflict),
        nameof(IInvocationMetrics.PermanentFault),
    ];

    private static JsonElement Publish(int recordCount, Action<IInvocationMetrics> record)
    {
        using var capture = new EmbeddedMetricsCapture();

        using (var metrics = capture.Publisher.BeginInvocation(recordCount))
        {
            record(metrics);
        }

        return capture.SingleRecord;
    }

    private static void RecordPermanent(IInvocationMetrics metrics, string method, int delivery)
    {
        var duration = TimeSpan.FromMilliseconds(9);

        switch (method)
        {
            case nameof(IInvocationMetrics.InvalidEvent):
                metrics.InvalidEvent(delivery, duration);
                break;
            case nameof(IInvocationMetrics.IdempotencyConflict):
                metrics.IdempotencyConflict(delivery, duration);
                break;
            case nameof(IInvocationMetrics.PermanentFault):
                metrics.PermanentFault(delivery, duration);
                break;
            default:
                Assert.Fail($"{method} has no case here. Add one alongside the outcome.");
                break;
        }
    }
}
