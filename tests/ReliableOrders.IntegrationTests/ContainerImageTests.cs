namespace ReliableOrders.IntegrationTests;

/// <summary>
/// The pinned emulator images, and the three places that have to name each of them.
/// </summary>
/// <remarks>
/// No container is started here, so these run in the ordinary build gate rather than carrying the
/// integration trait. Pinning is the kind of thing that decays quietly, and a check that only runs in
/// the slower workflow is a check that notices late.
/// </remarks>
public sealed class ContainerImageTests
{
    /// <summary>
    /// Every image a fixture starts, so a new emulator is covered by adding it here rather than by
    /// remembering to write two more tests.
    /// </summary>
    public static TheoryData<string> Images => [DynamoDbFixture.Image, LocalStackFixture.Image];

    /// <summary>
    /// Where the local development stack is defined.
    /// </summary>
    private const string ComposeFile = "compose.yaml";

    /// <summary>
    /// The image is pinned to a digest, not to a moving tag.
    /// </summary>
    /// <remarks>
    /// These tests certify that the emulator reports accurate cancellation reason codes and returns
    /// the conflicting item, which Stories 2.2 and 2.3 classify from and never re-read. On a moving
    /// tag, a future push could change that behaviour — and the dangerous direction is not a red build
    /// but a green one, certifying a guarantee that no longer holds.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Images))]
    public void The_image_is_pinned_to_a_digest(string image)
    {
        Assert.Contains("@sha256:", image, StringComparison.Ordinal);

        Assert.DoesNotContain(":latest@", image, StringComparison.Ordinal);
        Assert.False(
            image.EndsWith(":latest", StringComparison.Ordinal),
            "The emulator image is on a moving tag, so a future push could change the behaviour these "
            + "tests exist to certify.");
    }

    /// <summary>
    /// The workflow pre-pulls exactly the images the fixtures start.
    /// </summary>
    /// <remarks>
    /// The pre-pull exists so a registry failure is reported as itself rather than as a container that
    /// would not start. If the two references drift, it pulls one image and warms nothing for the
    /// other, which is a slower and more confusing failure than having no pre-pull at all.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Images))]
    public void The_integration_workflow_pulls_the_image_the_fixture_starts(string image)
    {
        var workflow = ReadRepositoryFile(Path.Combine(".github", "workflows", "integration.yml"));

        Assert.Contains(image, workflow, StringComparison.Ordinal);
    }

    /// <summary>
    /// The workflow names the variable the LocalStack fixture reads its auth token from.
    /// </summary>
    /// <remarks>
    /// Two names for one secret, in files that are edited for unrelated reasons. If they drift, the
    /// container gets no token and exits with code 55 before its edge port opens, which reads as a
    /// container that would not start rather than as a secret that was not passed.
    /// </remarks>
    [Fact]
    public void The_integration_workflow_passes_the_auth_token_the_fixture_reads()
    {
        var workflow = ReadRepositoryFile(Path.Combine(".github", "workflows", "integration.yml"));

        Assert.Contains(LocalStackFixture.AuthTokenVariable, workflow, StringComparison.Ordinal);
    }

    /// <summary>
    /// The workflow can exclude the tests that need a token, using the trait they carry.
    /// </summary>
    /// <remarks>
    /// The fallback path exists for pull requests from forks, which GitHub gives no access to
    /// repository secrets. A filter written against a trait name that no longer matches selects
    /// nothing and the step passes, reporting a green run over tests that were never executed — so
    /// the two are held together here rather than discovered in a fork's pull request.
    /// </remarks>
    [Fact]
    public void The_integration_workflow_filters_on_the_trait_the_tests_carry()
    {
        var workflow = ReadRepositoryFile(Path.Combine(".github", "workflows", "integration.yml"));

        Assert.Contains(
            $"Category!={TestCategory.RequiresLocalStackToken}",
            workflow,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The local development stack starts the image the fixture starts.
    /// </summary>
    /// <remarks>
    /// A third place naming each digest, and the one furthest from these tests. The stack is what a
    /// reader runs by hand to watch the flows, so an emulator that drifted there would be
    /// demonstrating behaviour the suite never certified — and doing it convincingly, since the flows
    /// would still appear to pass.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Images))]
    public void The_local_stack_starts_the_image_the_fixture_starts(string image)
    {
        var compose = ReadRepositoryFile(ComposeFile);

        Assert.Contains(image, compose, StringComparison.Ordinal);
    }

    /// <summary>
    /// The local development stack names the variable the LocalStack fixture reads its token from.
    /// </summary>
    /// <remarks>
    /// The same drift the workflow case above guards, arriving by a different route. The Compose file
    /// makes the variable required, so a rename leaves a stack that refuses to start at all with a
    /// message naming a variable nothing sets — which is loud, but loud about the wrong thing.
    /// </remarks>
    [Fact]
    public void The_local_stack_passes_the_auth_token_the_fixture_reads()
    {
        var compose = ReadRepositoryFile(ComposeFile);

        Assert.Contains(LocalStackFixture.AuthTokenVariable, compose, StringComparison.Ordinal);
    }

    /// <summary>
    /// The overlay for a machine behind a TLS interceptor names the variable the fixture reads.
    /// </summary>
    /// <remarks>
    /// A separate file because Compose cannot express an optional mount, and separate files are what
    /// drift. It is also the one knob nobody exercises until they are already stuck: it is unset in
    /// CI and on most machines, so a rename here would be found by whoever is least able to diagnose
    /// it — someone whose container is exiting 55 behind a corporate proxy.
    /// </remarks>
    [Fact]
    public void The_interceptor_overlay_mounts_the_certificate_the_fixture_reads()
    {
        var overlay = ReadRepositoryFile(Path.Combine("local", "compose.ca-bundle.yaml"));

        Assert.Contains(LocalStackFixture.CaBundleVariable, overlay, StringComparison.Ordinal);
    }

    /// <summary>
    /// Walks up from the test assembly to the repository root, identified by the solution file.
    /// </summary>
    /// <remarks>
    /// Neither file is copied beside the test assembly, and copying either would defeat the point —
    /// the assertions have to read the files CI and <c>docker compose</c> actually run.
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
