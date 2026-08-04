using System.Text.Json;

namespace ReliableOrders.ArchitectureTests;

/// <summary>
/// ReliableOrders.Core stays transport-neutral. Amazon.Lambda.SQSEvents is AWS-specific even though
/// it is not the SDK, so the whole AWS package family is banned. See the Repository Structure
/// section of docs/architecture.md.
/// </summary>
public sealed class CoreLayeringTests
{
    /// <summary>
    /// Covers every AWS-published family, not only AWSSDK and Amazon.Lambda. Amazon.Extensions.*
    /// carries configuration and dependency injection helpers that read as ordinary plumbing.
    /// </summary>
    private static readonly string[] ForbiddenPrefixes = ["AWSSDK.", "AWSXRayRecorder", "AWS.", "Amazon."];

    /// <summary>
    /// The lock file records the full restore graph, direct and transitive, and is unaffected by
    /// PrivateAssets. A package marked PrivateAssets="all" flows no assets to consumers, so it never
    /// reaches a consumer's output directory, yet Core still compiles against it.
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
    /// The lock file covers packages. This covers assemblies arriving by another route, such as a
    /// project reference from Core to an AWS-carrying project. This assembly references Core alone,
    /// so every file beside it came from Core, the test framework, or the runtime.
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
