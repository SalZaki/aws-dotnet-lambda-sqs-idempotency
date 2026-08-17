using System.Diagnostics;
using ReliableOrders.Core.Observability;

namespace ReliableOrders.UnitTests.Observability;

/// <summary>
/// Collects the spans this service starts, by listening to the real source.
/// </summary>
/// <remarks>
/// <para>
/// A listener rather than a stand-in for <see cref="ActivitySource"/>. The source is static and the
/// call sites hold no seam, which is deliberate — <see cref="ActivityListener"/> is the seam, and
/// subscribing to it means these tests observe exactly the spans a collector would.
/// </para>
/// <para>
/// A listener is process-wide, and xUnit runs test classes in parallel, so this will see spans from
/// whatever else is running at the same time. Nothing here tries to prevent that; the accessors below
/// select by identity instead, which is reliable where a global filter would not be. Every test
/// therefore has to look its spans up by something unique to it.
/// </para>
/// </remarks>
internal sealed class SpanCapture : IDisposable
{
    private readonly ActivityListener _listener;
    private readonly List<Activity> _spans = [];
    private readonly Lock _gate = new();

    public SpanCapture()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => string.Equals(source.Name, Tracing.SourceName, StringComparison.Ordinal),

            // Everything, recorded. A sampler is a production concern and one here would make these
            // tests depend on a decision they are not about.
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,

            // Collected on stop rather than on start, so a span's attributes and status are the final
            // ones. Most of what these tests assert is written after the span begins.
            ActivityStopped = Collect,
        };

        ActivitySource.AddActivityListener(_listener);
    }

    /// <summary>
    /// The record span for one message, which every other assertion is reached from.
    /// </summary>
    /// <exception cref="InvalidOperationException">No span, or more than one, carries that identifier.</exception>
    public Activity RecordFor(string messageId)
    {
        lock (_gate)
        {
            return _spans.Single(span =>
                span.OperationName == Tracing.Spans.ProcessRecord
                && span.GetTagItem(Tracing.Attributes.MessagingMessageId) as string == messageId);
        }
    }

    /// <summary>
    /// Whether any record span exists for a message.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="RecordFor"/> because a test asserting that no span was started needs
    /// an answer rather than an exception.
    /// </remarks>
    public bool HasRecordFor(string messageId)
    {
        lock (_gate)
        {
            return _spans.Exists(span =>
                span.OperationName == Tracing.Spans.ProcessRecord
                && span.GetTagItem(Tracing.Attributes.MessagingMessageId) as string == messageId);
        }
    }

    /// <summary>
    /// The names of the spans belonging to one trace, in the order they finished.
    /// </summary>
    /// <remarks>
    /// Selected by trace identifier, which is what keeps a parallel test's spans out of the answer.
    /// Order is completion order, so a step that contains another finishes after it.
    /// </remarks>
    public IReadOnlyList<string> NamesInTrace(ActivityTraceId traceId)
    {
        lock (_gate)
        {
            return [.. _spans.Where(span => span.TraceId == traceId).Select(span => span.OperationName)];
        }
    }

    /// <summary>
    /// The one span of a given name in a trace.
    /// </summary>
    public Activity InTrace(ActivityTraceId traceId, string name)
    {
        lock (_gate)
        {
            return _spans.Single(span => span.TraceId == traceId && span.OperationName == name);
        }
    }

    public void Dispose() => _listener.Dispose();

    private void Collect(Activity activity)
    {
        lock (_gate)
        {
            _spans.Add(activity);
        }
    }
}
