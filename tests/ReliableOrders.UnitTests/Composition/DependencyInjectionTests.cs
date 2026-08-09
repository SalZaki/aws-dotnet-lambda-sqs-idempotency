using Amazon.DynamoDBv2;
using Microsoft.Extensions.DependencyInjection;
using ReliableOrders.Aws.Sqs;
using ReliableOrders.Core.Observability;
using ReliableOrders.Core.Persistence;
using ReliableOrders.Core.Processing;
using ReliableOrders.Function;
using ReliableOrders.Function.Configuration;

namespace ReliableOrders.UnitTests.Composition;

/// <summary>
/// What the composition root builds, and how long it keeps it.
/// </summary>
/// <remarks>
/// These resolve real services against a real provider. Nothing here calls AWS — constructing an
/// <see cref="AmazonDynamoDBClient"/> resolves a region and credentials but opens no connection — so
/// the graph is exercised without a network.
/// </remarks>
public sealed class DependencyInjectionTests
{
    /// <summary>
    /// The whole graph resolves, which is what a cold start does before any message arrives.
    /// </summary>
    /// <remarks>
    /// The provider is built with validation on, so a missing registration or a lifetime mistake
    /// fails here rather than on the first record.
    /// </remarks>
    [Fact]
    public void The_handler_and_everything_under_it_resolve()
    {
        using var provider = DependencyInjection.Build(Configuration());

        Assert.NotNull(provider.GetRequiredService<SqsBatchHandler>());
        Assert.NotNull(provider.GetRequiredService<IOrderMessageProcessor>());
        Assert.NotNull(provider.GetRequiredService<IOrderCommandStore>());
        Assert.NotNull(provider.GetRequiredService<ProcessingLog>());
    }

    /// <summary>
    /// The AWS client is built once and reused, which is the story's own criterion.
    /// </summary>
    /// <remarks>
    /// A client per invocation would resolve credentials and a region every time and open a fresh
    /// connection pool, adding that cost to every record and exhausting sockets under load.
    /// </remarks>
    [Fact]
    public void The_dynamodb_client_is_shared_across_invocations()
    {
        using var provider = DependencyInjection.Build(Configuration());

        Assert.Same(
            provider.GetRequiredService<IAmazonDynamoDB>(),
            provider.GetRequiredService<IAmazonDynamoDB>());
    }

    /// <summary>
    /// So is everything else the handler depends on.
    /// </summary>
    /// <remarks>
    /// Asserted per service rather than trusting the registrations, because a single
    /// <c>AddTransient</c> among the singletons would rebuild part of the graph per resolve and show
    /// up only as latency.
    /// </remarks>
    [Fact]
    public void The_handler_graph_is_built_once()
    {
        using var provider = DependencyInjection.Build(Configuration());

        Assert.Same(provider.GetRequiredService<SqsBatchHandler>(), provider.GetRequiredService<SqsBatchHandler>());
        Assert.Same(
            provider.GetRequiredService<IOrderMessageProcessor>(),
            provider.GetRequiredService<IOrderMessageProcessor>());
        Assert.Same(provider.GetRequiredService<ProcessingLog>(), provider.GetRequiredService<ProcessingLog>());
    }

    /// <summary>
    /// The configured values reach the services that use them.
    /// </summary>
    [Fact]
    public void The_configured_table_names_reach_the_store()
    {
        using var provider = DependencyInjection.Build(Configuration());

        var tables = provider.GetRequiredService<Aws.DynamoDb.DynamoDbTableNames>();

        Assert.Equal("orders", tables.OrdersTableName);
        Assert.Equal("idempotency", tables.IdempotencyTableName);
    }

    /// <summary>
    /// A configuration the environment could not supply never reaches the graph.
    /// </summary>
    [Fact]
    public void Building_without_a_configuration_is_a_caller_defect()
    {
        Assert.Throws<ArgumentNullException>(() => DependencyInjection.Build(configuration: null!));
    }

    private static FunctionConfiguration Configuration()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [FunctionConfiguration.OrdersTableNameVariable] = "orders",
            [FunctionConfiguration.IdempotencyTableNameVariable] = "idempotency",
            [FunctionConfiguration.ServiceNameVariable] = "reliable-orders",
            [FunctionConfiguration.EnvironmentVariable] = "test",
            [FunctionConfiguration.MetricsNamespaceVariable] = "ReliableOrders",
        };

        return FunctionConfiguration.From(name => values.TryGetValue(name, out var value) ? value : null);
    }
}
