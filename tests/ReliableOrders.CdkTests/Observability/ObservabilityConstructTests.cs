using Amazon.CDK.Assertions;
using ReliableOrders.Cdk.Configuration;
using ReliableOrders.Cdk.Constructs;
using ReliableOrders.Core.Observability;

namespace ReliableOrders.CdkTests.Observability;

/// <summary>
/// What the dashboard asks CloudWatch to render, and whether it queries metrics this service emits.
/// </summary>
/// <remarks>
/// The widget assertions read the synthesised dashboard body rather than the construct's properties,
/// for the reason the rest of this suite does: a widget list the construct returns is the list the
/// test handed it, whichever body synthesis produced.
/// </remarks>
public sealed class ObservabilityConstructTests
{
    /// <summary>
    /// Every widget docs/observability.md asks for, by the title it carries on the dashboard.
    /// </summary>
    /// <remarks>
    /// One title per entry in the specification's list, in its order. A widget folded into another
    /// drops a title from the body and fails here.
    /// </remarks>
    public static TheoryData<string> RequiredWidgets =>
    [
        "SQS visible messages",
        "SQS messages in flight",
        "Age of oldest source message",
        "DLQ visible messages",
        "Lambda invocations",
        "Lambda errors",
        "Lambda throttles",
        "Lambda duration",
        "Lambda concurrent executions",
        "DynamoDB consumed capacity",
        "DynamoDB throttled requests",
        "Processed, duplicate, conflict and failure",
        "Per-record latency",
        "Deadline deferrals",
    ];

    /// <summary>
    /// The specified widget is on the dashboard.
    /// </summary>
    [Theory]
    [MemberData(nameof(RequiredWidgets))]
    public void The_dashboard_carries_every_specified_widget(string title)
    {
        Assert.Contains(title, Body(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The dashboard is named for the deployment, so two environments in one account stay apart.
    /// </summary>
    [Fact]
    public void The_dashboard_is_named_for_the_environment()
    {
        var dashboard = Template().OnlyResource(SynthesizedStack.DashboardResourceType);

        Assert.Equal(
            $"reliable-orders-{EnvironmentConfig.Development.EnvironmentName}",
            dashboard.Properties["DashboardName"]);
    }

    /// <summary>
    /// The custom widgets query the namespace and dimensions the publisher writes.
    /// </summary>
    /// <remarks>
    /// Asserted as one tuple rather than as four separate contains, because each part passes on its own
    /// for the wrong reason. The service name is a prefix of every queue name already in the body, and a
    /// dimension key appears wherever any metric is declared. Only the tuple shows the metric carrying
    /// the namespace and both dimensions together, which is what decides whether the widget renders.
    /// </remarks>
    [Fact]
    public void The_custom_widgets_query_the_namespace_and_dimensions_the_publisher_writes()
    {
        var expected =
            $@"[""{OrderProcessorConstruct.MetricsNamespace}"",""{ObservabilityConstruct.OrdersProcessedMetric}"","
            + $@"""{ObservabilityConstruct.EnvironmentDimension}"",""{EnvironmentConfig.Development.EnvironmentName}"","
            + $@"""{ObservabilityConstruct.ServiceDimension}"",""{OrderProcessorConstruct.ServiceName}""";

        Assert.Contains(expected, Unescaped(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The names the dashboard queries are the names the function emits.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The construct declares these itself rather than referencing <see cref="MetricNames"/>, so that
    /// synthesising infrastructure does not pull in the application assembly. This case is what makes
    /// that duplication safe: it is the only place both projects are in scope together, so a rename on
    /// either side fails the build instead of emptying a widget in the deployed dashboard.
    /// </para>
    /// <para>
    /// Asserted pairwise rather than as a set, so a failure names the metric that moved.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_queried_metric_names_are_the_ones_the_function_emits()
    {
        Assert.Equal(MetricNames.OrdersProcessed, ObservabilityConstruct.OrdersProcessedMetric);
        Assert.Equal(MetricNames.DuplicateEvents, ObservabilityConstruct.DuplicateEventsMetric);
        Assert.Equal(MetricNames.IdempotencyConflicts, ObservabilityConstruct.IdempotencyConflictsMetric);
        Assert.Equal(MetricNames.ValidationFailures, ObservabilityConstruct.ValidationFailuresMetric);
        Assert.Equal(MetricNames.PermanentFaults, ObservabilityConstruct.PermanentFaultsMetric);
        Assert.Equal(MetricNames.TransientFailures, ObservabilityConstruct.TransientFailuresMetric);
        Assert.Equal(MetricNames.RecordProcessingLatency, ObservabilityConstruct.RecordProcessingLatencyMetric);
        Assert.Equal(MetricNames.DeadlineDeferrals, ObservabilityConstruct.DeadlineDeferralsMetric);
    }

    /// <summary>
    /// The dimension keys are the ones the publisher dimensions its metrics by.
    /// </summary>
    /// <remarks>
    /// Pinned for the same reason as the metric names, and separately because the publisher restricts
    /// itself to exactly these two. A third dimension on either side makes every custom widget query a
    /// series that does not exist.
    /// </remarks>
    [Fact]
    public void The_queried_dimensions_are_the_ones_the_publisher_writes()
    {
        Assert.Equal(LogFields.Service, ObservabilityConstruct.ServiceDimension);
        Assert.Equal(LogFields.Environment, ObservabilityConstruct.EnvironmentDimension);
    }

    /// <summary>
    /// The stack declares one dashboard rather than one per construct that wanted widgets.
    /// </summary>
    [Fact]
    public void The_stack_declares_exactly_one_dashboard()
    {
        Assert.Single(Template().FindResources(SynthesizedStack.DashboardResourceType));
    }

    /// <summary>
    /// The throttle widget queries metrics DynamoDB publishes against a table.
    /// </summary>
    /// <remarks>
    /// <c>ThrottledRequests</c> is published only against <c>(TableName, Operation)</c>, so a
    /// table-dimensioned query for it matches no series and renders flat. Its absence is asserted
    /// alongside the replacements, because reinstating it breaks nothing a positive assertion sees.
    /// </remarks>
    [Fact]
    public void The_throttle_widget_queries_table_level_metrics()
    {
        var body = Body();

        Assert.Contains(ObservabilityConstruct.ReadThrottleEventsMetric, body, StringComparison.Ordinal);
        Assert.Contains(ObservabilityConstruct.WriteThrottleEventsMetric, body, StringComparison.Ordinal);
        Assert.DoesNotContain("ThrottledRequests", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The dashboard name is a stack output, which is how a runbook finds it.
    /// </summary>
    /// <remarks>
    /// Required by docs/infrastructure.md alongside the queue, table and function outputs. The name
    /// carries the environment suffix, so without the output an operator is searching the console for
    /// a dashboard whose name they are trying to work out.
    /// </remarks>
    [Fact]
    public void The_dashboard_name_is_a_stack_output()
    {
        Assert.NotEmpty(Template().FindOutputs("DashboardName"));
    }

    private static Template Template() => SynthesizedStack.From(EnvironmentConfig.Development);

    /// <summary>
    /// The dashboard body as the template carries it, which is an intrinsic rather than a plain string
    /// because widget metrics reference queue and table names the stack generates.
    /// </summary>
    private static string Body() =>
        Template().OnlyResource(SynthesizedStack.DashboardResourceType).Json("DashboardBody");

    /// <summary>
    /// The body with its JSON string escapes resolved, so an assertion can name a metric tuple the way
    /// CloudWatch reads it rather than the way the template serialises it.
    /// </summary>
    private static string Unescaped() => Body().Replace(@"\u0022", @"""", StringComparison.Ordinal);
}
