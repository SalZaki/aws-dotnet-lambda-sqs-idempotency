using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using ReliableOrders.Core.Observability;
using ReliableOrders.Function.Configuration;

namespace ReliableOrders.Function.Observability;

/// <summary>
/// Turns the spans this service starts into exported telemetry.
/// </summary>
/// <remarks>
/// <para>
/// The only place an OpenTelemetry SDK is referenced. Core and Aws start their spans from
/// <see cref="System.Diagnostics.ActivitySource"/>, which is in the framework, so nothing below the
/// composition root knows an exporter exists — and when none is configured those spans cost a null
/// check. Where telemetry goes is a hosting decision, and this is where hosting decisions live.
/// </para>
/// <para>
/// Exported over OTLP to the ADOT collector running as a Lambda layer beside the function, which
/// forwards to X-Ray. Nothing here names an endpoint. The SDK reads the standard
/// <c>OTEL_EXPORTER_OTLP_*</c> variables and defaults to the collector's own address, so redirecting
/// the export is a deployment change rather than a code change, and the deployment already has to set
/// the layer.
/// </para>
/// <para>
/// <c>OTEL_SDK_DISABLED=true</c> turns the whole thing off, which is the standard switch and the
/// reason no bespoke one exists. Spans are still started when it is set — an
/// <see cref="System.Diagnostics.ActivitySource"/> with no listener returns null and allocates
/// nothing.
/// </para>
/// </remarks>
public static class TracingConfiguration
{
    /// <summary>
    /// Registers the tracer provider, which the container then builds and owns.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Registered as a singleton rather than built and forgotten, because a
    /// <see cref="TracerProvider"/> that is collected stops exporting. The container holds it for the
    /// life of the execution environment, which is the same lifetime as the DynamoDB client beside it.
    /// </para>
    /// <para>
    /// One effect of the registration outlives the container. <c>AddXRayTraceId</c> replaces the
    /// process-wide <see cref="System.Diagnostics.Activity.TraceIdGenerator"/>, and disposing the
    /// provider does not put the original back — so a test process that builds a provider generates
    /// X-Ray-shaped identifiers from then on. Nothing here depends on the shape, and there is no
    /// supported way to undo it; it is recorded because a reader tracing an identifier's origin would
    /// otherwise look for a listener that is no longer attached.
    /// </para>
    /// <para>
    /// The exporter batches, and the batch is flushed by the entry point at the end of every
    /// invocation. It has to be. The collector layer owns a buffer, but only once spans have reached
    /// it — before that they sit in this process's batch queue, whose worker thread Lambda freezes the
    /// moment the handler returns. Left to the default five-second schedule, a record processed near
    /// the end of an invocation waits for a thaw that may never come, and on a quiet queue that is
    /// most traces rather than an edge case.
    /// </para>
    /// <para>
    /// Flushed rather than exported synchronously per span. A simple processor would put a round trip
    /// to the collector inside every one of the six spans a record produces, on a path measured
    /// against a deadline; one flush per invocation pays that cost once, after the last record.
    /// </para>
    /// </remarks>
    /// <param name="services">The collection being built.</param>
    /// <param name="configuration">Supplies the service name and environment the spans are tagged with.</param>
    /// <returns>The same collection, so configuration can be chained.</returns>
    public static IServiceCollection AddTracing(
        this IServiceCollection services,
        FunctionConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Behind a factory rather than registered as an already-built instance, because a factory's
        // result is the only thing the container disposes: an instance it did not create it does not
        // own, and a provider that outlives its container leaves this process's listener attached to the
        // source and the exporter's worker thread running.
        //
        // A factory runs on first resolve, and nothing here resolves a TracerProvider — the components
        // that trace hold an ActivitySource instead and never ask for one. So the composition root
        // resolves it once, immediately after building the provider, which is both what attaches the
        // listener and what puts it under the container's ownership. Neither half works alone: without
        // the factory the exporter is never shut down, and without that resolve nothing is ever
        // constructed and every span is dropped by a service whose composition root looks correctly
        // wired. See DependencyInjection.Build.
        services.AddSingleton(_ => Sdk.CreateTracerProviderBuilder()
            // X-Ray-shaped trace identifiers, which are not what OpenTelemetry generates by default.
            // X-Ray reads the first four bytes of a trace identifier as a Unix timestamp and rejects
            // anything outside roughly a month, so a random W3C identifier is refused by the
            // collector's exporter almost every time. The failure is silent in the worst way: the
            // function exports, the collector drops, the invocation succeeds, and X-Ray shows nothing.
            //
            // This fixes the identifiers this service originates. A record continuing a publisher's
            // trace inherits that publisher's identifier, so the publisher has to generate them the
            // same way for the trace to survive the exporter — see the Tracing Specification.
            .AddXRayTraceId()

            .ConfigureResource(resource => resource
                .AddService(configuration.ServiceName)
                .AddAttributes([
                    new KeyValuePair<string, object>(DeploymentEnvironmentAttribute, configuration.Environment),
                ]))

            // The one source this service starts spans from. A source nobody subscribes to produces
            // nothing, so a rename that missed this line would empty every trace while the build
            // stayed green — which is why the name is read off the type rather than written again.
            .AddSource(Tracing.SourceName)

            // The AWS SDK's own spans, which is what puts the DynamoDB call itself in the trace under
            // the persist span rather than leaving persist as an opaque interval.
            .AddAWSInstrumentation()

            // Endpoint, protocol and headers all come from the standard environment variables. The
            // default is the collector beside this function.
            .AddOtlpExporter()
            .Build()!);

        return services;
    }

    /// <summary>
    /// The resource attribute naming the deployment, matching the environment dimension on the metrics
    /// and the environment field on every log line.
    /// </summary>
    /// <remarks>
    /// Spelled here rather than taken from the SDK's constants, which have moved between releases as
    /// the semantic conventions stabilised. A literal that a reader can compare against a dashboard is
    /// worth more than a symbol that silently changes value on upgrade.
    /// </remarks>
    private const string DeploymentEnvironmentAttribute = "deployment.environment.name";
}
