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

    /// <summary>
    /// Every alarm docs/observability.md requires is declared, by the name it carries.
    /// </summary>
    /// <remarks>
    /// The composite is the eighth and is a different CloudFormation type, so it is asserted below
    /// rather than here. The two alarms it is built from are legs rather than entries in the list.
    /// </remarks>
    [Theory]
    [InlineData("DeadLetterQueueNotEmpty")]
    [InlineData("IdempotencyConflicts")]
    [InlineData("SourceQueueBacklog")]
    [InlineData("FunctionThrottled")]
    [InlineData("TransientFailures")]
    [InlineData("TableThrottlingOrErrors")]
    [InlineData("DeadlineDeferrals")]
    public void The_specified_alarm_is_declared(string name)
    {
        Assert.Contains(
            $"reliable-orders-{EnvironmentConfig.Development.EnvironmentName}-{name}",
            AlarmNames(),
            StringComparer.Ordinal);
    }

    /// <summary>
    /// The thresholds an alarm deploys with are the configured ones.
    /// </summary>
    /// <remarks>
    /// Read off the template against the configuration, so a threshold hard-coded in the construct
    /// fails here. Three of the five are checked; the other two are evaluation-period counts, covered
    /// by the case below.
    /// </remarks>
    [Fact]
    public void The_alarm_thresholds_are_the_configured_ones()
    {
        var thresholds = EnvironmentConfig.Development.AlarmThresholds;

        Assert.Equal(thresholds.OldestMessageAgeSeconds, Threshold("SourceQueueBacklog"));
        Assert.Equal(thresholds.TransientFailuresPerFiveMinutes, Threshold("TransientFailures"));
        Assert.Equal(thresholds.DeadlineDeferralsPerFiveMinutes, Threshold("DeadlineDeferrals"));
    }

    /// <summary>
    /// The two counted windows are the configured ones, expressed in evaluation periods.
    /// </summary>
    /// <remarks>
    /// The throttle alarm counts minutes, so its window is the configured number. The no-progress legs
    /// count aggregation periods, and the window they cover is asserted by multiplying back rather than
    /// by dividing the configured value the same way the construct does. Dividing here would compare a
    /// truncation against itself: a twelve minute window deploys as two periods, and an expectation of
    /// <c>12 / 5</c> is also two, so the case would pass while the alarm watched ten minutes.
    /// </remarks>
    [Fact]
    public void The_counted_windows_are_the_configured_ones()
    {
        var thresholds = EnvironmentConfig.Development.AlarmThresholds;

        Assert.Equal(thresholds.ThrottleEvaluationMinutes, Periods("FunctionThrottled"));

        Assert.Equal(
            thresholds.NoProgressMinutes,
            Periods("NoProgressQueueNotEmpty") * AlarmThresholds.AggregationPeriodMinutes);

        Assert.Equal(
            thresholds.NoProgressMinutes,
            Periods("NoProgressNothingProcessed") * AlarmThresholds.AggregationPeriodMinutes);
    }

    /// <summary>
    /// The no-progress alarm fires only when both of its legs do.
    /// </summary>
    /// <remarks>
    /// Either leg alone is a healthy state. A queue with messages on it is normal, and no processing is
    /// normal when nothing has arrived, so an AND that became an OR would page on both.
    /// </remarks>
    [Fact]
    public void The_no_progress_alarm_is_the_conjunction_of_its_two_legs()
    {
        var composite = Template().OnlyResource(SynthesizedStack.CompositeAlarmResourceType);
        var rule = composite.Json("AlarmRule");

        Assert.Contains("AND", rule, StringComparison.Ordinal);
        Assert.Contains("NoProgressQueueNotEmpty", rule, StringComparison.Ordinal);
        Assert.Contains("NoProgressNothingProcessed", rule, StringComparison.Ordinal);
    }

    /// <summary>
    /// A gap in the processed metrics is the outage, not an absence of news.
    /// </summary>
    /// <remarks>
    /// OrdersProcessed and DuplicateEvents are published even when zero precisely so this leg has
    /// datapoints during an outage. Left not-breaching like every other alarm here, the one condition
    /// it watches for would report insufficient data instead of firing.
    /// </remarks>
    [Fact]
    public void The_no_progress_leg_treats_missing_data_as_breaching()
    {
        Assert.Equal("breaching", Alarm("NoProgressNothingProcessed").Properties["TreatMissingData"]);
        Assert.Equal("notBreaching", Alarm("DeadLetterQueueNotEmpty").Properties["TreatMissingData"]);
    }

    /// <summary>
    /// Every alarm notifies the topic, and the composite's legs do not.
    /// </summary>
    /// <remarks>
    /// An alarm with no action deploys, renders green or red on the console, and pages nobody. The legs
    /// are the deliberate exception: they are states the composite is built from, and notifying on each
    /// would send the two messages the composite exists to replace.
    /// </remarks>
    [Fact]
    public void Every_alarm_notifies_the_topic_except_the_composites_legs()
    {
        var template = Template();

        foreach (var (logicalId, resource) in template.FindResources(SynthesizedStack.AlarmResourceType))
        {
            var properties = SynthesizedStack.Object(resource["Properties"], logicalId);
            var name = (string)properties["AlarmName"];
            var notifies = properties.ContainsKey("AlarmActions");

            Assert.Equal(!name.Contains("NoProgress", StringComparison.Ordinal), notifies);
        }

        Assert.True(
            template.OnlyResource(SynthesizedStack.CompositeAlarmResourceType)
                .Properties.ContainsKey("AlarmActions"));
    }

    /// <summary>
    /// The topic subscribes the configured endpoint.
    /// </summary>
    /// <remarks>
    /// The address is configuration rather than a deploy-time parameter, so the subscription is created
    /// with the stack. The development value is a reserved domain and can never be confirmed, which is
    /// deliberate: the repository is public.
    /// </remarks>
    [Fact]
    public void The_topic_subscribes_the_configured_endpoint()
    {
        var subscription = Template().OnlyResource(SynthesizedStack.SubscriptionResourceType);

        Assert.Equal("email", subscription.Properties["Protocol"]);
        Assert.Equal(EnvironmentConfig.Development.AlarmEndpoint, subscription.Properties["Endpoint"]);
    }

    /// <summary>
    /// One topic, so that a second subscriber is added in one place.
    /// </summary>
    [Fact]
    public void The_stack_declares_one_alarm_topic()
    {
        Assert.Single(Template().FindResources(SynthesizedStack.TopicResourceType));
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

    /// <summary>
    /// Every metric alarm's name, as the template declares it.
    /// </summary>
    private static IEnumerable<string> AlarmNames() =>
        Template().FindResources(SynthesizedStack.AlarmResourceType)
            .Select(entry => (string)SynthesizedStack.Object(entry.Value["Properties"], entry.Key)["AlarmName"]);

    /// <summary>
    /// The alarm carrying the given unprefixed name.
    /// </summary>
    private static SynthesizedResource Alarm(string name)
    {
        var full = $"reliable-orders-{EnvironmentConfig.Development.EnvironmentName}-{name}";

        foreach (var (logicalId, resource) in Template().FindResources(SynthesizedStack.AlarmResourceType))
        {
            var properties = SynthesizedStack.Object(resource["Properties"], logicalId);

            if (string.Equals((string)properties["AlarmName"], full, StringComparison.Ordinal))
            {
                return new SynthesizedResource(logicalId, properties, resource);
            }
        }

        throw new InvalidOperationException($"No alarm named '{full}' in the template.");
    }

    private static int Threshold(string name) => Alarm(name).Number("Threshold");

    private static int Periods(string name) => Alarm(name).Number("EvaluationPeriods");
}
