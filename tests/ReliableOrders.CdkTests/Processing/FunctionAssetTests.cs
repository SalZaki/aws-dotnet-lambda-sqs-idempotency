using ReliableOrders.Cdk.Configuration;

namespace ReliableOrders.CdkTests.Processing;

/// <summary>
/// What happens when the function has not been published.
/// </summary>
/// <remarks>
/// The whole value of not bundling the function during synthesis is that the publish step is
/// somebody's explicit responsibility. That only holds if forgetting it fails loudly, so the two ways
/// of forgetting are the cases here. Both assert on the message, because a throw an operator cannot
/// act on leaves them no better off than a deployment that fails later.
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
}
