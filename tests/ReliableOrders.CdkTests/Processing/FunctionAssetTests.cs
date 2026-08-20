using ReliableOrders.Cdk.Configuration;

namespace ReliableOrders.CdkTests.Processing;

/// <summary>
/// What happens when the function has not been published.
/// </summary>
/// <remarks>
/// The whole value of not bundling the function during synthesis is that the publish step is
/// somebody's explicit responsibility. That only holds if forgetting it fails loudly, so the ways of
/// forgetting are the cases here. Each asserts on the message, because a throw an operator cannot act
/// on leaves them no better off than a deployment that fails later.
/// </remarks>
public sealed class FunctionAssetTests
{
    /// <summary>
    /// An unpublished function fails naming the command that publishes it.
    /// </summary>
    [Fact]
    public void An_absent_publish_directory_is_refused()
    {
        var root = Path.Combine(Path.GetTempPath(), $"reliable-orders-absent-{Guid.NewGuid():N}");

        var exception = Assert.Throws<InvalidOperationException>(() => FunctionAsset.FromPublishOutput(root));

        Assert.Contains(FunctionAsset.PublishCommand, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A publish directory without the function assembly is refused, rather than packaged.
    /// </summary>
    /// <remarks>
    /// What an interrupted publish, or one to another configuration, leaves behind. CDK will zip
    /// whatever is there and the deployment succeeds — the handler is simply absent, and says so on
    /// the first message rather than at synthesis. The dependencies are written into the fixture so
    /// the case is about the missing assembly rather than about an empty directory.
    /// </remarks>
    [Fact]
    public void A_publish_directory_without_the_function_assembly_is_refused()
    {
        var root = Path.Combine(Path.GetTempPath(), $"reliable-orders-partial-{Guid.NewGuid():N}");

        var published = Path.Combine(root, FunctionAsset.PublishDirectory);

        Directory.CreateDirectory(published);
        File.WriteAllText(Path.Combine(published, "AWSSDK.DynamoDBv2.dll"), "a dependency, and not the handler");

        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() => FunctionAsset.FromPublishOutput(root));

            Assert.Contains(FunctionAsset.FunctionAssembly, exception.Message, StringComparison.Ordinal);
            Assert.Contains(FunctionAsset.PublishCommand, exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// A publish directory without the runtime configuration is refused, rather than packaged.
    /// </summary>
    /// <remarks>
    /// Not an interrupted publish but a complete one, of a project that stopped asking for the file.
    /// A class library emits no runtime configuration by default, and the .NET Lambda runtime refuses
    /// a package without it before the handler is resolved — so the deployment succeeds, every
    /// invocation fails, and the error names a JSON file rather than anything about this function.
    /// The assembly is written into the fixture so the case is about the missing configuration.
    /// </remarks>
    [Fact]
    public void A_publish_directory_without_the_runtime_configuration_is_refused()
    {
        var root = Path.Combine(Path.GetTempPath(), $"reliable-orders-unloadable-{Guid.NewGuid():N}");

        var published = Path.Combine(root, FunctionAsset.PublishDirectory);

        Directory.CreateDirectory(published);
        File.WriteAllText(Path.Combine(published, FunctionAsset.FunctionAssembly), "the handler, unloadable");

        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() => FunctionAsset.FromPublishOutput(root));

            Assert.Contains(FunctionAsset.RuntimeConfiguration, exception.Message, StringComparison.Ordinal);
            Assert.Contains(FunctionAsset.PublishCommand, exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// The published function carries both files, which is what the two cases above are about.
    /// </summary>
    /// <remarks>
    /// The one case here that reads the real publish output rather than a fixture. Without it, the
    /// two above would keep passing after a project change that stopped producing the runtime
    /// configuration — they assert that its absence is refused, not that it is ever present. Skipped
    /// rather than failed when the function has not been published, because these tests run in the
    /// build gate and the publish is a separate step.
    /// </remarks>
    [Fact]
    public void The_published_function_carries_both_files()
    {
        var published = Path.Combine(RepositoryFiles.Root, FunctionAsset.PublishDirectory);

        Assert.SkipUnless(
            Directory.Exists(published),
            $"The function has not been published to '{published}'. Run {FunctionAsset.PublishCommand}.");

        Assert.True(
            File.Exists(Path.Combine(published, FunctionAsset.RuntimeConfiguration)),
            $"'{published}' holds no {FunctionAsset.RuntimeConfiguration}. A class library emits none "
            + "unless GenerateRuntimeConfigurationFiles is set, and the Lambda runtime refuses a "
            + "package without it. Check ReliableOrders.Function.csproj.");

        Assert.True(
            File.Exists(Path.Combine(published, FunctionAsset.FunctionAssembly)),
            $"'{published}' holds no {FunctionAsset.FunctionAssembly}.");
    }

}
