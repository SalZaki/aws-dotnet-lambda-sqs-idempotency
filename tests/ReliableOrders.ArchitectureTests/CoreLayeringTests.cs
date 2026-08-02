using System.Text.Json;

namespace ReliableOrders.ArchitectureTests;

/// <summary>
/// Guards the one structural rule the rest of the design leans on: ReliableOrders.Core stays
/// transport-neutral. Amazon.Lambda.SQSEvents is AWS-specific even though it is not the SDK, so the
/// whole AWS package family is banned. See the Repository Structure section of docs/architecture.md.
/// </summary>
public sealed class CoreLayeringTests
{
    /// <summary>
    /// Covers every AWS-published family, not only the two named in the story. Amazon.Extensions.*
    /// is the one most likely to be reached for by accident, because it carries the dependency
    /// injection integration that looks like ordinary plumbing rather than an AWS dependency.
    /// </summary>
    private static readonly string[] ForbiddenPrefixes = ["AWSSDK.", "AWSXRayRecorder", "AWS.", "Amazon."];

    /// <summary>
    /// The lock file is the authority on what Core resolves. It records the full restore graph,
    /// direct and transitive, and is unaffected by PrivateAssets — which is what makes it the right
    /// source here. A package marked PrivateAssets="all" flows no assets to consumers and so never
    /// appears in a consumer's output directory, yet Core still compiles against it.
    /// </summary>
    [Fact]
    public void Core_declares_no_aws_package_in_its_restore_graph()
    {
        var lockFile = Path.Combine(AppContext.BaseDirectory, CorePackagesLockFileName);

        Assert.True(
            File.Exists(lockFile),
            $"Expected {CorePackagesLockFileName} beside the test assembly. This test reads "
            + "ReliableOrders.Core's lock file; without it the layering rule is unverified rather "
            + "than satisfied. Check RestorePackagesWithLockFile in Directory.Build.props and the "
            + "None Include in this project.");

        var offenders = ReadPackageNames(lockFile)
            .Where(HasForbiddenPrefix)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            $"ReliableOrders.Core must not depend on AWS packages. Found: {string.Join(", ", offenders)}. "
            + "Move the adapter into ReliableOrders.Aws and express the need in Core as an interface.");
    }

    /// <summary>
    /// A second angle on the same rule. The lock file covers packages; this covers assemblies
    /// arriving by any other route, such as a project reference from Core to an AWS-carrying
    /// project. This assembly references Core alone, so every file beside it came from Core, from
    /// the test framework, or from the runtime.
    /// </summary>
    [Fact]
    public void Core_brings_no_aws_assembly_into_its_output()
    {
        var offenders = Directory
            .EnumerateFiles(AppContext.BaseDirectory, "*.dll", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(HasForbiddenPrefix)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            $"ReliableOrders.Core must not carry AWS assemblies. Found: {string.Join(", ", offenders)}.");
    }

    private const string CorePackagesLockFileName = "ReliableOrders.Core.packages.lock.json";

    private static IEnumerable<string> ReadPackageNames(string lockFilePath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(lockFilePath));

        if (!document.RootElement.TryGetProperty("dependencies", out var frameworks))
        {
            yield break;
        }

        foreach (var framework in frameworks.EnumerateObject())
        {
            foreach (var package in framework.Value.EnumerateObject())
            {
                yield return package.Name;
            }
        }
    }

    private static bool HasForbiddenPrefix(string name) =>
        ForbiddenPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
}
