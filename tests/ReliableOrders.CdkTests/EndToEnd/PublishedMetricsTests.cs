using ReliableOrders.Cdk.Constructs;
using ReliableOrders.EndToEndTests;

namespace ReliableOrders.CdkTests.EndToEnd;

/// <summary>
/// The names the end-to-end suite queries CloudWatch with, against the names the stack deploys.
/// </summary>
/// <remarks>
/// Two strings, copied rather than referenced for the reason that suite gives, and this is what stops
/// them becoming stale copies. A namespace or dimension changed in the construct and not there would
/// leave every metric assertion querying a series nothing publishes — which reads as a sum of zero,
/// and a sum of zero is what a broken publisher looks like too.
/// </remarks>
public sealed class PublishedMetricsTests
{
    [Fact]
    public void The_end_to_end_suite_queries_the_namespace_the_stack_publishes_under()
    {
        Assert.Equal(OrderProcessorConstruct.MetricsNamespace, DeploymentQueries.MetricsNamespace);
    }

    [Fact]
    public void The_end_to_end_suite_queries_the_service_dimension_the_stack_publishes()
    {
        Assert.Equal(OrderProcessorConstruct.ServiceName, DeploymentQueries.ServiceName);
    }
}
