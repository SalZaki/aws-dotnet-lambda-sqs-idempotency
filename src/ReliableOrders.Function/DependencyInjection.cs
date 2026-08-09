using Amazon.DynamoDBv2;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReliableOrders.Aws.DynamoDb;
using ReliableOrders.Aws.Sqs;
using ReliableOrders.Aws.Telemetry;
using ReliableOrders.Core.Contracts;
using ReliableOrders.Core.Idempotency;
using ReliableOrders.Core.Observability;
using ReliableOrders.Core.Persistence;
using ReliableOrders.Core.Processing;
using ReliableOrders.Core.Validation;
using ReliableOrders.Function.Configuration;
using ReliableOrders.Function.Observability;

namespace ReliableOrders.Function;

/// <summary>
/// Builds everything the function needs, once per execution environment.
/// </summary>
/// <remarks>
/// <para>
/// Once, not per invocation. An <see cref="AmazonDynamoDBClient"/> holds a connection pool and its
/// construction resolves credentials and a region, so building one per message would add that cost to
/// every record and exhaust sockets under load. Everything here is a singleton for the same reason,
/// and nothing holds per-invocation state — the metrics accumulator that does is created by the
/// handler when an invocation begins.
/// </para>
/// <para>
/// Configuration is read first and validated before anything is constructed, so a cold start with a
/// missing variable fails naming it rather than part-building a graph.
/// </para>
/// </remarks>
public static class DependencyInjection
{
    /// <summary>
    /// Builds the service provider from the process environment.
    /// </summary>
    /// <inheritdoc cref="FunctionConfiguration.FromEnvironment"/>
    public static ServiceProvider Build() => Build(FunctionConfiguration.FromEnvironment());

    /// <summary>
    /// Builds the service provider from a supplied configuration.
    /// </summary>
    /// <param name="configuration">What the environment said, already validated.</param>
    /// <returns>The provider, which the caller keeps for the life of the execution environment.</returns>
    public static ServiceProvider Build(FunctionConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var services = new ServiceCollection();

        services.AddSingleton(configuration);
        services.AddLogging(builder => builder.AddJsonStdoutLogging(configuration.LogLevel));

        // Injected rather than read from DateTimeOffset.UtcNow, and used only for latency and the
        // invocation deadline. No value written inside the transaction derives from it; the store
        // takes no clock at all, which is what keeps a retry's request body identical to the first
        // attempt's.
        services.AddSingleton(TimeProvider.System);

        services.AddSingleton<IAmazonDynamoDB>(_ => new AmazonDynamoDBClient());
        // The pair's own constructor rejects two names that are equal, but FunctionConfiguration has
        // already refused that case naming both environment variables — which is the message an
        // operator can act on, rather than one naming a constructor parameter.
        services.AddSingleton(new DynamoDbTableNames(
            configuration.OrdersTableName,
            configuration.IdempotencyTableName));
        services.AddSingleton(configuration.Retention);
        services.AddSingleton(configuration.SkewWindow);

        services.AddSingleton<IOrderEventParser, OrderEventParser>();
        services.AddSingleton<IOrderEventValidator, OrderEventValidator>();
        services.AddSingleton<IPayloadHasher, CanonicalPayloadHasher>();
        services.AddSingleton<IOrderCommandStore, DynamoDbOrderCommandStore>();

        services.AddSingleton(provider => new ProcessingLog(
            provider.GetRequiredService<ILogger<ProcessingLog>>(),
            configuration.ServiceName,
            configuration.Environment));

        // Standard output is the whole transport for metrics as it is for logs, so the publisher is
        // given Console.Out rather than a CloudWatch client. Nothing here calls PutMetricData.
        services.AddSingleton(provider => new EmbeddedMetricsPublisher(
            Console.Out,
            provider.GetRequiredService<TimeProvider>(),
            configuration.MetricsNamespace,
            configuration.ServiceName,
            configuration.Environment));

        services.AddSingleton<IOrderMessageProcessor, OrderMessageProcessor>();
        services.AddSingleton<SqsBatchHandler>();

        // Validated on build rather than on first resolve. A lifetime mistake — a singleton holding
        // something scoped — would otherwise surface as a failure on the first message rather than at
        // the cold start that built it.
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }
}
