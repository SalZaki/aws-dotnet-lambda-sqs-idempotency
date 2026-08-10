using System.Globalization;
using System.Text.Json;
using Amazon.CDK;
using Amazon.CDK.Assertions;
using ReliableOrders.Cdk.Configuration;
using ReliableOrders.Cdk.Stacks;

namespace ReliableOrders.CdkTests;

/// <summary>
/// Synthesises the stack and reads resources out of the resulting template.
/// </summary>
/// <remarks>
/// <para>
/// The assertions read the template rather than the construct's own properties. A construct returning
/// the value it was given proves nothing about what CloudFormation is asked to create, and the
/// properties that matter most here — the redrive policies — are only assembled during synthesis.
/// </para>
/// <para>
/// Resources are found by queue name, and a logical ID is only ever compared against one discovered
/// the same way. A test that pinned a logical ID would fail on a refactor that deployed identically.
/// </para>
/// </remarks>
internal static class SynthesizedStack
{
    /// <summary>The CloudFormation type both queues are declared as.</summary>
    public const string QueueResourceType = "AWS::SQS::Queue";

    /// <summary>The type a queue's resource policy is declared as, separately from the queue.</summary>
    public const string QueuePolicyResourceType = "AWS::SQS::QueuePolicy";

    /// <summary>
    /// Synthesises a stack built from the given configuration.
    /// </summary>
    /// <remarks>
    /// The account and Region are stated rather than left out. An environment-agnostic stack renders
    /// every ARN as a pseudo-parameter, a shape no deployment of this stack produces.
    /// </remarks>
    public static Template From(EnvironmentConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var app = new App(new AppProps { Context = DeployedContext() });

        return Template.FromStack(new ReliableOrdersStack(
            app,
            $"ReliableOrders-{config.EnvironmentName}",
            config,
            new StackProps
            {
                Env = new Amazon.CDK.Environment { Account = "111122223333", Region = "eu-west-2" },
            }));
    }

    /// <summary>
    /// Reads the context the CDK CLI would supply, which is where the feature flags live.
    /// </summary>
    private static Dictionary<string, object> DeployedContext()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "cdk.json");

        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"Expected cdk.json beside the test assembly at '{path}'. Without it these tests "
                + "synthesise without the feature flags the CLI applies, and assert a template no "
                + "deployment produces. Check the None Include in ReliableOrders.CdkTests.csproj.");
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));

        return document.RootElement.GetProperty("context")
            .EnumerateObject()
            .ToDictionary(property => property.Name, property => Value(property.Value), StringComparer.Ordinal);
    }

    /// <summary>
    /// Converts one context value into what jsii accepts.
    /// </summary>
    private static object Value(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => element.GetString()!,
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.Array => element.EnumerateArray().Select(Value).ToArray(),
            _ => throw new InvalidOperationException(
                $"A cdk.json context value is a {element.ValueKind}, which this reader does not carry."),
        };

    /// <summary>
    /// Returns the queue with the given name.
    /// </summary>
    /// <exception cref="InvalidOperationException">No queue in the template carries that name.</exception>
    public static SynthesizedQueue Queue(this Template template, string queueName)
    {
        ArgumentNullException.ThrowIfNull(template);

        var found = new List<string>();

        foreach (var (logicalId, resource) in template.FindResources(QueueResourceType))
        {
            var properties = resource.TryGetValue("Properties", out var declared)
                ? Object(declared, logicalId)
                : throw new InvalidOperationException($"Queue '{logicalId}' declares no properties.");

            if (properties.TryGetValue("QueueName", out var value) && value is string name)
            {
                if (string.Equals(name, queueName, StringComparison.Ordinal))
                {
                    return new SynthesizedQueue(logicalId, properties, resource);
                }

                found.Add(name);
            }
        }

        throw new InvalidOperationException(
            $"No queue named '{queueName}' in the template. Found: "
            + $"{(found.Count == 0 ? "none" : string.Join(", ", found))}.");
    }

    /// <summary>
    /// Returns the resource policy attached to a queue, rendered back to JSON.
    /// </summary>
    /// <remarks>
    /// The policy is a resource of its own rather than a property of the queue, so it is found by the
    /// queue it names. Rendering it keeps the assertion to the statement under test without pinning
    /// the shape of a document CDK assembles.
    /// </remarks>
    /// <exception cref="InvalidOperationException">No policy in the template names that queue.</exception>
    public static string PolicyFor(this Template template, SynthesizedQueue queue)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(queue);

        foreach (var (logicalId, resource) in template.FindResources(QueuePolicyResourceType))
        {
            var properties = resource.TryGetValue("Properties", out var declared)
                ? Object(declared, logicalId)
                : throw new InvalidOperationException($"Queue policy '{logicalId}' declares no properties.");

            if (JsonSerializer.Serialize(properties["Queues"]).Contains(queue.LogicalId, StringComparison.Ordinal))
            {
                return JsonSerializer.Serialize(properties["PolicyDocument"]);
            }
        }

        throw new InvalidOperationException($"No queue policy in the template names queue '{queue.LogicalId}'.");
    }

    /// <summary>
    /// Reads a value the template should carry as a JSON object.
    /// </summary>
    /// <param name="value">The value read out of the template.</param>
    /// <param name="context">What was being read, so a failure says where it came from.</param>
    /// <remarks>
    /// Null is reported rather than dereferenced, so an unexpected template shape fails naming what
    /// was being read instead of throwing a <see cref="NullReferenceException"/> from the helper meant
    /// to diagnose it.
    /// </remarks>
    internal static IDictionary<string, object> Object(object? value, string context) =>
        value switch
        {
            IDictionary<string, object> members => members,
            null => throw new InvalidOperationException($"Expected a JSON object for {context}, and it was null."),
            _ => throw new InvalidOperationException(
                $"Expected a JSON object for {context}, and the assertions library returned "
                + $"{value.GetType().Name}."),
        };
}

/// <summary>
/// One queue as the synthesised template declares it.
/// </summary>
/// <param name="LogicalId">What other resources reference it by.</param>
/// <param name="Properties">The resource's CloudFormation properties.</param>
/// <param name="Resource">The whole resource, which carries the policies CloudFormation keeps outside the properties.</param>
internal sealed record SynthesizedQueue(
    string LogicalId,
    IDictionary<string, object> Properties,
    IDictionary<string, object> Resource)
{
    /// <summary>
    /// What CloudFormation does with the queue when the stack no longer declares it.
    /// </summary>
    /// <remarks>
    /// A resource attribute rather than a property, so reading it off the properties would report it
    /// as absent on a queue that has one.
    /// </remarks>
    public string DeletionPolicy =>
        Resource.TryGetValue("DeletionPolicy", out var value) && value is string policy
            ? policy
            : throw new InvalidOperationException($"Queue '{LogicalId}' declares no deletion policy.");

    /// <summary>
    /// Reads a numeric property, which the template carries as a JSON number.
    /// </summary>
    public int Number(string name) =>
        Properties.TryGetValue(name, out var value)
            ? Convert.ToInt32(value, CultureInfo.InvariantCulture)
            : throw new InvalidOperationException($"Queue '{LogicalId}' has no '{name}' property.");

    /// <summary>
    /// Reads a boolean property.
    /// </summary>
    public bool Flag(string name) =>
        Properties.TryGetValue(name, out var value)
            ? Convert.ToBoolean(value, CultureInfo.InvariantCulture)
            : throw new InvalidOperationException($"Queue '{LogicalId}' has no '{name}' property.");

    /// <summary>
    /// Renders one property back to JSON.
    /// </summary>
    /// <remarks>
    /// Used for the properties CloudFormation assembles out of intrinsic functions, where the value is
    /// a nested <c>Fn::Join</c> or <c>Fn::GetAtt</c> rather than a string. Asserting on the rendered
    /// JSON keeps the case readable without pinning the exact shape of an intrinsic that CDK is free
    /// to emit differently.
    /// </remarks>
    public string Json(string name) =>
        Properties.TryGetValue(name, out var value)
            ? JsonSerializer.Serialize(value)
            : throw new InvalidOperationException($"Queue '{LogicalId}' has no '{name}' property.");

    /// <summary>
    /// Returns the queue's tags as the key-value pairs the template declares.
    /// </summary>
    public IReadOnlyDictionary<string, string> Tags()
    {
        if (!Properties.TryGetValue("Tags", out var value) || value is not IEnumerable<object> entries)
        {
            throw new InvalidOperationException($"Queue '{LogicalId}' carries no tags.");
        }

        var tags = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            var tag = SynthesizedStack.Object(entry, $"a tag on queue '{LogicalId}'");
            tags[(string)tag["Key"]] = (string)tag["Value"];
        }

        return tags;
    }
}
