using System.Runtime.CompilerServices;

namespace ReliableOrders.UnitTests.Composition;

/// <summary>
/// Gives the AWS SDK a region before any test resolves a client.
/// </summary>
/// <remarks>
/// <para>
/// The composition tests build the real provider, which constructs an <c>AmazonDynamoDBClient</c>.
/// Nothing calls it, but its constructor throws on a machine with no AWS configuration at all —
/// which is every CI runner.
/// </para>
/// <para>
/// A module initialiser rather than a line inside a test. Setting a process-wide variable from a test
/// body leaks into every other test in the assembly, and xunit runs collections in parallel, so the
/// leak is not even deterministic. This runs once before anything else and defers to a value already
/// set, so it cannot override a developer's own region.
/// </para>
/// </remarks>
internal static class AwsRegionDefault
{
    private const string Variable = "AWS_REGION";

    [ModuleInitializer]
    internal static void Apply()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable)))
        {
            Environment.SetEnvironmentVariable(Variable, "eu-west-2");
        }
    }
}
