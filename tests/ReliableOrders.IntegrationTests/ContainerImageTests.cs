namespace ReliableOrders.IntegrationTests;

/// <summary>
/// The pinned emulator image, and the two places that have to name the same one.
/// </summary>
/// <remarks>
/// No container is started here, so these run in the ordinary build gate rather than carrying the
/// integration trait. Pinning is the kind of thing that decays quietly, and a check that only runs in
/// the slower workflow is a check that notices late.
/// </remarks>
public sealed class ContainerImageTests
{
    /// <summary>
    /// The image is pinned to a digest, not to a moving tag.
    /// </summary>
    /// <remarks>
    /// These tests certify that the emulator reports accurate cancellation reason codes and returns
    /// the conflicting item, which Stories 2.2 and 2.3 classify from and never re-read. On a moving
    /// tag, a future push could change that behaviour — and the dangerous direction is not a red build
    /// but a green one, certifying a guarantee that no longer holds.
    /// </remarks>
    [Fact]
    public void The_image_is_pinned_to_a_digest()
    {
        Assert.Contains("@sha256:", DynamoDbFixture.Image, StringComparison.Ordinal);

        Assert.DoesNotContain(":latest@", DynamoDbFixture.Image, StringComparison.Ordinal);
        Assert.False(
            DynamoDbFixture.Image.EndsWith(":latest", StringComparison.Ordinal),
            "The emulator image is on a moving tag, so a future push could change the behaviour these "
            + "tests exist to certify.");
    }

    /// <summary>
    /// The workflow pre-pulls exactly the image the fixture starts.
    /// </summary>
    /// <remarks>
    /// The pre-pull exists so a registry failure is reported as itself rather than as a container that
    /// would not start. If the two references drift, it pulls one image and warms nothing for the
    /// other, which is a slower and more confusing failure than having no pre-pull at all.
    /// </remarks>
    [Fact]
    public void The_integration_workflow_pulls_the_image_the_fixture_starts()
    {
        var workflow = ReadRepositoryFile(Path.Combine(".github", "workflows", "integration.yml"));

        Assert.Contains(DynamoDbFixture.Image, workflow, StringComparison.Ordinal);
    }

    /// <summary>
    /// Walks up from the test assembly to the repository root, identified by the solution file.
    /// </summary>
    /// <remarks>
    /// The workflow is not copied beside the test assembly, and copying it would defeat the point —
    /// the assertion has to read the file CI actually runs.
    /// </remarks>
    private static string ReadRepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ReliableOrders.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        var path = Path.Combine(directory.FullName, relativePath);

        Assert.True(File.Exists(path), $"Expected {relativePath} at {path}.");

        return File.ReadAllText(path);
    }
}
