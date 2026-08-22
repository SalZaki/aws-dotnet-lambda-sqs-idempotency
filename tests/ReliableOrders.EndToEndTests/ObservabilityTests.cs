using ReliableOrders.Core.Observability;

namespace ReliableOrders.EndToEndTests;

/// <summary>
/// What an operator sees after a message has been processed.
/// </summary>
/// <remarks>
/// The runbooks open with these fields and these metrics, so a deployment that processes correctly
/// and reports nothing is a deployment nobody can operate. Asserted against CloudWatch rather than
/// against the formatter's unit tests, because what is being checked is that the line survived the
/// runtime, the log format and the extraction — the three places it has been lost before.
/// </remarks>
[Trait(TestCategory.Name, TestCategory.EndToEnd)]
public sealed class ObservabilityTests(Deployment deployment) : IClassFixture<Deployment>
{
    /// <summary>
    /// The processed line carries the fields a runbook searches on.
    /// </summary>
    [RequiresDeployment]
    public async Task A_processed_record_is_logged_with_its_identifiers()
    {
        var published = EndToEndEvents.New(Run);

        _ = await deployment.Send(published);

        Assert.NotNull(await deployment.Order(published.Data.OrderId));

        var lines = await deployment.LogLines(
            $"{{ $.{LogFields.EventId} = \"{published.EventId}\" }}",
            atLeast: 1,
            DeploymentQueries.WriteVisible);

        var line = Assert.Single(lines);

        foreach (var field in new[]
                 {
                     LogFields.Service,
                     LogFields.Environment,
                     LogFields.EventId,
                     LogFields.OrderId,
                     LogFields.CorrelationId,
                     LogFields.Outcome,
                 })
        {
            Assert.Contains($"\"{field}\"", line.Message, StringComparison.Ordinal);
        }

        // The payload is not in the line. A log that carried the event body would put customer data
        // into a group with a month's retention and a different audience from the queue it came from.
        Assert.DoesNotContain(published.Data.ItemDescription, line.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(published.Data.CustomerId, line.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The metric the dashboard opens with is published, under the dimensions it reads.
    /// </summary>
    /// <remarks>
    /// Dimensioned rather than summed across the namespace, because the dashboard and the alarms are
    /// dimensioned. A metric published without them would be invisible to both while a namespace-wide
    /// query still found it.
    /// </remarks>
    [RequiresDeployment]
    public async Task A_processed_record_is_counted_where_the_dashboard_reads()
    {
        var since = DateTimeOffset.UtcNow.AddMinutes(-1);
        var published = EndToEndEvents.New(Run);

        _ = await deployment.Send(published);

        Assert.NotNull(await deployment.Order(published.Data.OrderId));

        var processed = await deployment.MetricSum(MetricNames.OrdersProcessed, since);

        Assert.True(processed >= 1, $"{MetricNames.OrdersProcessed} summed to {processed} for this run.");
    }

    /// <summary>What distinguishes this run's orders from another's.</summary>
    private static string Run { get; } = Deployment.EnvironmentName;
}
