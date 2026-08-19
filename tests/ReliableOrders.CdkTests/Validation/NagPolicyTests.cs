using System.Text.Json;
using Amazon.CDK;
using ReliableOrders.Cdk.Configuration;
using ReliableOrders.Cdk.Stacks;

namespace ReliableOrders.CdkTests.Validation;

/// <summary>
/// The stack passes the rule pack every synthesis is checked against.
/// </summary>
/// <remarks>
/// <para>
/// The pack is registered by <see cref="NagPolicy"/> rather than here, so these cases exercise what
/// <c>cdk synth</c> and <c>cdk deploy</c> run rather than a second configuration that could drift
/// from it.
/// </para>
/// <para>
/// A violation fails synthesis, so the first case would pass or fail on its own. It reads the report
/// on the way out because "synthesis threw" names a file in a temporary directory, and the rule that
/// was broken is what the reader needs.
/// </para>
/// </remarks>
public sealed class NagPolicyTests
{
    /// <summary>
    /// The rules this stack accepts findings for, and the only ones.
    /// </summary>
    /// <remarks>
    /// Pinned so that accepting a third is a deliberate edit here rather than a line in a construct
    /// nobody reviews. Both are argued where they are declared: the X-Ray actions define no resource
    /// to scope to, and point-in-time recovery is a per-environment setting this environment turned
    /// off.
    /// </remarks>
    private static readonly string[] AcceptedRules =
    [
        "AwsSolutions-DDB3",
        "AwsSolutions-IAM5[Resource::*]",
    ];

    /// <summary>
    /// Synthesis raises no finding that has not been accepted.
    /// </summary>
    [Fact]
    public void The_stack_synthesises_without_an_unaccepted_finding()
    {
        var failure = Record.Exception(Synthesise);

        if (failure is null)
        {
            return;
        }

        Assert.Fail($"cdk-nag rejected the stack: {string.Join(", ", BrokenRules(failure))}.");
    }

    /// <summary>
    /// Every accepted rule is one this file names.
    /// </summary>
    /// <remarks>
    /// Read off the construct tree rather than from a list the constructs also read, so a suppression
    /// added anywhere in the stack shows up here. The acknowledgement metadata is where the CDK
    /// records them, and the reason travels with the identifier — an acceptance without one would be
    /// refused by <see cref="NagPolicy.Accept"/> before it reached this.
    /// </remarks>
    [Fact]
    public void Only_the_named_rules_are_accepted()
    {
        var accepted = AcknowledgedRules().Order(StringComparer.Ordinal).ToArray();

        Assert.Equal<IEnumerable<string>>(AcceptedRules, accepted);
    }

    private static void Synthesise() => Stack().Item1.Synth();

    /// <summary>
    /// Every rule acknowledged anywhere in the stack, however deep it was declared.
    /// </summary>
    private static IEnumerable<string> AcknowledgedRules() =>
        Stack().Item2.Node.FindAll()
            .SelectMany(construct => construct.Node.Metadata)
            .Where(entry => string.Equals(
                entry.Type,
                Validations.ACKNOWLEDGED_RULES_METADATA_KEY,
                StringComparison.Ordinal))
            .SelectMany(entry => Rules(entry.Data))

            // The CDK qualifies a bare identifier with its own annotation prefix, which the pack
            // strips before it compares. Stripped here for the same reason: what is asserted should be
            // the rule as the report names it.
            .Select(rule => rule.StartsWith("Annotation::", StringComparison.OrdinalIgnoreCase)
                ? rule["Annotation::".Length..]
                : rule)
            .Distinct(StringComparer.Ordinal);

    private static IEnumerable<string> Rules(object? data) =>
        data is IDictionary<string, object> entries ? entries.Keys : [];

    /// <summary>
    /// The rule names inside the validation report the failure points at.
    /// </summary>
    private static IEnumerable<string> BrokenRules(Exception failure)
    {
        var path = System.Text.RegularExpressions.Regex
            .Match(failure.Message, @"found in: (\S+\.json)").Groups[1].Value;

        if (!File.Exists(path))
        {
            return [failure.Message];
        }

        using var report = JsonDocument.Parse(File.ReadAllText(path));

        return
        [
            .. report.RootElement.GetProperty("pluginReports").EnumerateArray()
                .SelectMany(plugin => plugin.GetProperty("violations").EnumerateArray())
                .Select(violation => violation.GetProperty("ruleName").GetString() ?? "unnamed rule"),
        ];
    }

    /// <summary>
    /// The app the policy is applied to, and the stack inside it.
    /// </summary>
    private static (App, Stack) Stack()
    {
        var config = EnvironmentConfig.Development;
        var app = NagPolicy.Apply(SynthesizedStack.NewApp());

        var stack = new ReliableOrdersStack(
            app,
            $"ReliableOrders-{config.EnvironmentName}",
            config,
            SynthesizedStack.FunctionCode(),
            new StackProps { Env = SynthesizedStack.TestEnvironment });

        return (app, stack);
    }
}
