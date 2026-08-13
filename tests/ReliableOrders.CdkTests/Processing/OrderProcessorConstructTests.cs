using System.Globalization;
using Amazon.CDK.Assertions;
using ReliableOrders.Cdk.Configuration;
using ReliableOrders.Cdk.Constructs;
using ReliableOrders.Function.Configuration;

namespace ReliableOrders.CdkTests.Processing;

/// <summary>
/// What the processor construct asks CloudFormation to create.
/// </summary>
/// <remarks>
/// None of the numbers here are the development defaults, so a construct that ignored its
/// configuration would fail rather than pass on values nobody chose.
/// </remarks>
public sealed class OrderProcessorConstructTests
{
    private const string EnvironmentName = "assert";
    private const string RuntimeIdentifier = "dotnet10";

    private const int MemoryMb = 1024;
    private const int TimeoutSeconds = 45;
    private const int ReservedConcurrency = 20;
    private const int BatchSize = 7;
    private const int BatchWindowSeconds = 3;
    private const int MaxConcurrency = 15;
    private const int IdempotencyRetentionDays = 21;

    /// <summary>
    /// The function runs the configured runtime, at the configured size, on the documented handler.
    /// </summary>
    /// <remarks>
    /// The runtime is asserted against the configured string rather than a CDK constant, which is the
    /// point of reading it from configuration — a fallback to an earlier managed runtime should not
    /// need an edit to the construct or to this case.
    /// </remarks>
    [Fact]
    public void The_function_runs_the_configured_runtime_and_size()
    {
        var function = Function();

        Assert.Equal(RuntimeIdentifier, function.Properties["Runtime"]);
        Assert.Equal(MemoryMb, function.Number("MemorySize"));
        Assert.Equal(TimeoutSeconds, function.Number("Timeout"));
        Assert.Equal(ReservedConcurrency, function.Number("ReservedConcurrentExecutions"));
        // Text, because the function writes its own JSON. Lambda's JSON format would wrap each stdout
        // line, which puts the EMF record's "_aws" key below the root and stops CloudWatch extracting
        // any metric from it — silently, with the line still in the log.
        Assert.Equal("Text", function.Properties["LoggingConfig"] is IDictionary<string, object> logging
            ? logging["LogFormat"]
            : null);
    }

    /// <summary>
    /// The handler string names a method that exists.
    /// </summary>
    /// <remarks>
    /// Built from the type rather than compared against the constant, which would be the constant
    /// compared with itself. This is the one value in the template that no cold-start check covers —
    /// rename <c>Function</c> or <c>HandleAsync</c> and everything compiles, synthesises and deploys,
    /// then fails on the first message with a handler the runtime cannot find.
    /// </remarks>
    [Fact]
    public void The_handler_names_the_entry_point_that_exists()
    {
        var entryPoint = typeof(ReliableOrders.Function.Function);
        var method = nameof(ReliableOrders.Function.Function.HandleAsync);

        Assert.Equal(
            $"{entryPoint.Assembly.GetName().Name}::{entryPoint.FullName}::{method}",
            Function().Properties["Handler"]);
    }

    /// <summary>
    /// X-Ray active tracing is off.
    /// </summary>
    /// <remarks>
    /// Absence is the assertion. CDK emits no <c>TracingConfig</c> for a disabled function, and
    /// switching it on writes <c>Mode: Active</c> here — so the property appearing at all is the
    /// regression, which is the second tracing pipeline docs/observability.md forbids.
    /// </remarks>
    [Fact]
    public void The_function_does_not_trace_with_xray()
    {
        Assert.DoesNotContain("TracingConfig", Function().Properties.Keys, StringComparer.Ordinal);
    }

    /// <summary>
    /// Every variable the function requires at cold start is set.
    /// </summary>
    /// <remarks>
    /// The names come from <c>FunctionConfiguration</c> itself rather than from string literals here,
    /// so renaming one fails this build rather than the first invocation after a deployment. The
    /// optional variables are deliberately absent — their defaults belong to the type that owns them.
    /// </remarks>
    [Fact]
    public void The_function_is_given_every_variable_it_requires()
    {
        var variables = Variables();

        foreach (var required in new[]
        {
            FunctionConfiguration.OrdersTableNameVariable,
            FunctionConfiguration.IdempotencyTableNameVariable,
            FunctionConfiguration.ServiceNameVariable,
            FunctionConfiguration.EnvironmentVariable,
            FunctionConfiguration.MetricsNamespaceVariable,
        })
        {
            Assert.Contains(required, variables.Keys, StringComparer.Ordinal);
        }

        Assert.Equal(EnvironmentName, variables[FunctionConfiguration.EnvironmentVariable]);
        Assert.Equal(OrderProcessorConstruct.ServiceName, variables[FunctionConfiguration.ServiceNameVariable]);
        Assert.Equal(
            OrderProcessorConstruct.MetricsNamespace,
            variables[FunctionConfiguration.MetricsNamespaceVariable]);

        // The retention horizon is a deployment decision rather than a code default, so it is set, and
        // it is asserted because nothing else would notice the wrong number. A retention taken from
        // another field expires idempotency records early, which re-admits the duplicates the whole
        // design exists to reject, and no CDK property would look wrong.
        Assert.Equal(
            IdempotencyRetentionDays.ToString(CultureInfo.InvariantCulture),
            variables[FunctionConfiguration.IdempotencyRetentionDaysVariable]);

        // The remaining optional ones are absent on purpose. Restating a default here would put a
        // second copy of it in the deployment, where it outranks the one FunctionConfiguration argues
        // for.
        Assert.DoesNotContain(FunctionConfiguration.LogLevelVariable, variables.Keys, StringComparer.Ordinal);

        Assert.DoesNotContain(
            FunctionConfiguration.MaxEventSkewFutureHoursVariable,
            variables.Keys,
            StringComparer.Ordinal);
    }

    /// <summary>
    /// The two table names the function is given are the tables this stack created.
    /// </summary>
    /// <remarks>
    /// A literal name here would pass against a function pointed at a table in another stack, which is
    /// the mistake worth catching — the tables carry no physical name precisely so that nothing can
    /// guess one.
    /// </remarks>
    [Fact]
    public void The_function_is_pointed_at_this_stack_s_tables()
    {
        var template = Template();
        var variables = Variables(template);

        Assert.Contains(
            template.TableKeyedOn(PersistenceConstruct.OrderIdAttribute).LogicalId,
            variables[FunctionConfiguration.OrdersTableNameVariable],
            StringComparison.Ordinal);

        Assert.Contains(
            template.TableKeyedOn(PersistenceConstruct.IdempotencyKeyAttribute).LogicalId,
            variables[FunctionConfiguration.IdempotencyTableNameVariable],
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The event source reports partial batch failures, and reads the source queue.
    /// </summary>
    /// <remarks>
    /// Without <c>ReportBatchItemFailures</c> one failed record redelivers the other nine, which turns
    /// a single poison message into a batch that never drains. It is the reason the batch handler
    /// returns a failure list at all.
    /// </remarks>
    [Fact]
    public void The_event_source_reports_batch_item_failures()
    {
        var template = Template();
        var mapping = template.OnlyResource("AWS::Lambda::EventSourceMapping");

        Assert.Equal(BatchSize, mapping.Number("BatchSize"));
        Assert.Equal(BatchWindowSeconds, mapping.Number("MaximumBatchingWindowInSeconds"));
        Assert.Equal("[\"ReportBatchItemFailures\"]", mapping.Json("FunctionResponseTypes"));

        Assert.Contains(
            template.Queue($"reliable-orders-{EnvironmentName}").LogicalId,
            mapping.Json("EventSourceArn"),
            StringComparison.Ordinal);

        // Event counts from the poller. Without them the only view of the source is the queue's own
        // metrics, which cannot tell a poller that stopped reading from a queue that stopped filling.
        Assert.Contains("EventCount", mapping.Json("MetricsConfig"), StringComparison.Ordinal);
    }

    /// <summary>
    /// The event source may not ask for more concurrency than the function is allowed.
    /// </summary>
    /// <remarks>
    /// <see cref="EnvironmentConfig"/> refuses the combination, so this asserts the template carries
    /// the two values it was given rather than that the rule exists. Both halves are needed: the
    /// invariant is only worth anything if these are the numbers that reach CloudFormation.
    /// </remarks>
    [Fact]
    public void The_event_source_asks_for_no_more_concurrency_than_the_function_allows()
    {
        var template = Template();

        var scaling = template.OnlyResource("AWS::Lambda::EventSourceMapping").Json("ScalingConfig");
        var reserved = Function(template).Number("ReservedConcurrentExecutions");

        Assert.Contains($"\"MaximumConcurrency\":{MaxConcurrency}", scaling, StringComparison.Ordinal);
        Assert.Equal(ReservedConcurrency, reserved);
        Assert.True(MaxConcurrency <= reserved, $"{MaxConcurrency} exceeds the reserved {reserved}.");
    }

    /// <summary>
    /// Log retention is stated, and the group goes the way of the data.
    /// </summary>
    /// <remarks>
    /// A function left to create its own log group gets one that never expires and bills for it
    /// forever, which is the failure the explicit group exists to avoid.
    /// </remarks>
    [Theory]
    [InlineData(true, "Retain")]
    [InlineData(false, "Delete")]
    public void The_log_group_states_its_retention(bool retainData, string expected)
    {
        var logs = Template(retainData).OnlyResource("AWS::Logs::LogGroup");

        Assert.Equal(30, logs.Number("RetentionInDays"));
        Assert.Equal(expected, logs.DeletionPolicy);
    }

    /// <summary>
    /// The stack publishes the function name.
    /// </summary>
    [Fact]
    public void The_stack_publishes_the_function_name()
    {
        Template().HasOutput("OrderProcessorFunctionName", Match.AnyValue());
    }

    /// <summary>
    /// The function's environment, with each value rendered back to what the template holds.
    /// </summary>
    /// <remarks>
    /// A value is a literal for the ones this stack decides and a <c>Ref</c> for the ones
    /// CloudFormation resolves, so they are read as JSON rather than as strings. A literal keeps its
    /// quotes off; a reference arrives as the object, which is what the table cases look inside.
    /// </remarks>
    private static Dictionary<string, string> Variables(Template? template = null)
    {
        var environment = SynthesizedStack.Object(
            Function(template).Properties["Environment"],
            "the function environment");

        var variables = SynthesizedStack.Object(environment["Variables"], "the environment variables");

        return variables.ToDictionary(
            entry => entry.Key,
            entry => entry.Value as string ?? System.Text.Json.JsonSerializer.Serialize(entry.Value),
            StringComparer.Ordinal);
    }

    private static SynthesizedResource Function(Template? template = null) =>
        (template ?? Template()).OnlyResource("AWS::Lambda::Function");

    private static Template Template(bool retainData = false) => SynthesizedStack.From(Config(retainData));

    private static EnvironmentConfig Config(bool retainData = false) => new(
        environmentName: EnvironmentName,
        lambdaRuntimeIdentifier: RuntimeIdentifier,
        lambdaMemoryMb: MemoryMb,
        lambdaTimeoutSeconds: TimeoutSeconds,
        reservedConcurrency: ReservedConcurrency,
        batchSize: BatchSize,
        batchWindowSeconds: BatchWindowSeconds,
        maxConcurrency: MaxConcurrency,
        visibilityMarginSeconds: 29,
        maxReceiveCount: 5,
        sourceRetentionDays: 4,
        dlqRetentionDays: 14,
        idempotencyRetentionDays: IdempotencyRetentionDays,
        retainData: retainData,
        enablePointInTimeRecovery: false);
}
