using Amazon.Lambda.Core;
using Amazon.Lambda.Serialization.SystemTextJson;
using Amazon.Lambda.SQSEvents;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Trace;
using ReliableOrders.Aws.Sqs;
using ReliableOrders.Function.Serialization;

[assembly: LambdaSerializer(typeof(SourceGeneratorLambdaJsonSerializer<LambdaSerializerContext>))]

namespace ReliableOrders.Function;

/// <summary>
/// The entry point the Lambda runtime invokes.
/// </summary>
/// <remarks>
/// <para>
/// A class-library handler rather than an executable assembly, so the runtime loads this type and the
/// serializer arrives by the assembly-level attribute above. That keeps the entry point free of
/// bootstrap plumbing. An executable using <c>LambdaBootstrapBuilder</c> becomes the right shape only
/// if Native AOT is adopted, which is a later benchmark rather than a decision made here — recording
/// the choice is what stops the two styles being mixed.
/// </para>
/// <para>
/// The service provider is static and built once, so it survives across invocations and the DynamoDB
/// client with it. It is a <see cref="Lazy{T}"/> rather than a static field initialiser because a
/// field initialiser that throws is wrapped in a <c>TypeInitializationException</c>, and the message
/// naming the missing variable would end up one level down where an operator has to go looking. A
/// lazy rethrows the original, and caches it, so every later attempt reports the same cause.
/// </para>
/// </remarks>
public sealed class Function
{
    private static readonly Lazy<ServiceProvider> Services = new(DependencyInjection.Build);

    private readonly SqsBatchHandler _handler;
    private readonly TimeProvider _clock;
    private readonly TracerProvider? _tracer;

    /// <summary>
    /// How long the end-of-invocation flush is given.
    /// </summary>
    /// <remarks>
    /// The collector is in the same execution environment, so a healthy flush is nothing like this
    /// long. The bound is for the unhealthy case: a collector that is not listening must not hold the
    /// invocation open, and the deadline margin this eats into exists for exactly this kind of
    /// shutdown work.
    /// </remarks>
    private const int FlushTimeoutMilliseconds = 500;

    /// <summary>
    /// Constructed by the runtime, once per execution environment.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Configuration is missing or unusable. The message names the variable.
    /// </exception>
    public Function()
        : this(
            Services.Value.GetRequiredService<SqsBatchHandler>(),
            Services.Value.GetRequiredService<TimeProvider>(),
            Services.Value.GetRequiredService<TracerProvider>())
    {
    }

    /// <summary>
    /// Constructed around a handler directly, which is what lets the entry point be tested.
    /// </summary>
    /// <param name="handler">Processes one batch.</param>
    /// <param name="clock">
    /// The same clock the handler compares the deadline against. Taken rather than read from
    /// <see cref="TimeProvider.System"/> here, because a deadline computed from one clock and tested
    /// against another is a deadline no test can move: a handler holding a fake would compare a fake
    /// now with a real-clock instant, and a remaining time of zero would defer nothing while the test
    /// asserted that it had.
    /// </param>
    /// <param name="tracer">
    /// Flushed at the end of every invocation, or null in a test that is not about tracing. Optional
    /// because it is the one dependency here whose absence changes nothing an assertion would read:
    /// the spans are still started, and a test with no exporter has nothing to flush.
    /// </param>
    public Function(SqsBatchHandler handler, TimeProvider clock, TracerProvider? tracer = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(clock);

        _handler = handler;
        _clock = clock;
        _tracer = tracer;
    }

    /// <summary>
    /// Processes one SQS batch.
    /// </summary>
    /// <remarks>
    /// Turning the runtime's remaining time into a deadline happens here, because this is the only
    /// place that sees an <see cref="ILambdaContext"/>. The handler is given the instant, so nothing
    /// below this method depends on the Lambda runtime to know when to stop.
    /// </remarks>
    /// <param name="batch">The event as the runtime deserialised it.</param>
    /// <param name="context">The invocation, for its request identifier and remaining time.</param>
    /// <returns>The records to redeliver, by SQS message identifier.</returns>
    public async Task<SQSBatchResponse> HandleAsync(SQSEvent batch, ILambdaContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var invocation = new BatchInvocation(
            context.AwsRequestId,
            ProcessingDeadline.From(_clock.GetUtcNow(), context.RemainingTime));

        try
        {
            // No cancellation token exists at this boundary — the runtime offers none, and the
            // deadline is how this function stops itself. See BatchInvocation.
            return await _handler.HandleAsync(batch, invocation, CancellationToken.None);
        }
        finally
        {
            // Flushed here because this is the last line that runs before Lambda freezes the execution
            // environment, and a frozen environment stops the exporter's worker thread mid-batch. In a
            // finally, so an invocation that throws still ships the spans explaining why.
            _tracer?.ForceFlush(FlushTimeoutMilliseconds);
        }
    }
}
