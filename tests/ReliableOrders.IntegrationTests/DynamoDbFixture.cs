using Amazon.DynamoDBv2;
using Amazon.Runtime;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace ReliableOrders.IntegrationTests;

/// <summary>
/// One <c>amazon/dynamodb-local</c> container and one client, shared by every transaction test.
/// </summary>
/// <remarks>
/// <para>
/// The official Amazon image rather than LocalStack. Classification reads
/// <c>CancellationReasons[i].Code</c> and requires <c>CancellationReasons[i].Item</c> to be populated
/// when <c>ReturnValuesOnConditionCheckFailure</c> is set. LocalStack is not dependable on either
/// point, and a false pass there would hide the mechanism this project exists to demonstrate.
/// </para>
/// <para>
/// The container is started once per test collection rather than per test. Startup is seconds and the
/// tables are namespaced by key, so tests share them without colliding as long as each uses its own
/// order and event identifiers.
/// </para>
/// </remarks>
public sealed class DynamoDbFixture : IAsyncLifetime
{
    /// <summary>
    /// The port dynamodb-local listens on inside the container. The host port is assigned by Docker,
    /// so parallel runs and a developer's own container cannot collide.
    /// </summary>
    private const int DynamoDbPort = 8000;

    /// <summary>
    /// The emulator image, pinned to an immutable digest.
    /// </summary>
    /// <remarks>
    /// A moving tag would undo what these tests are for. They certify that the emulator reports
    /// accurate cancellation reason codes and returns the conflicting item, which Stories 2.2 and 2.3
    /// classify from and never re-read. A future push to <c>amazon/dynamodb-local</c> could change that
    /// quietly, and the dangerous direction is not a red build — it is a green one certifying a
    /// guarantee that no longer holds.
    /// </remarks>
    /// <remarks>
    /// The tag is kept alongside the digest so a reader can see which version this is. Docker enforces
    /// the digest; the tag is documentation. <c>integration.yml</c> pre-pulls the same reference, and
    /// <c>ContainerImageTests</c> holds the two in step.
    /// </remarks>
    internal const string Image =
        "amazon/dynamodb-local:3.3.1@sha256:ff89bd48ff32cd8d9be5fee8873b65b8854dc408f1afe881be6eb00247bc0dab";

    private readonly IContainer _container = new ContainerBuilder(Image)
        .WithPortBinding(DynamoDbPort, assignRandomHostPort: true)
        // In-memory rather than a mounted data file. Each run starts from nothing, so a test cannot
        // pass because of a row an earlier run left behind.
        .WithCommand("-jar", "DynamoDBLocal.jar", "-inMemory")
        // Waits for the port inside the container to accept a connection, not merely for Docker to
        // report the container as running. dynamodb-local is a JVM process, so it is up well after
        // the container is, and starting a test in that gap fails as a connection refused.
        .WithWaitStrategy(
            Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(DynamoDbPort))
        .Build();

    /// <summary>
    /// A client pointed at the container, usable from any test in the collection.
    /// </summary>
    public IAmazonDynamoDB Client { get; private set; } = null!;

    /// <inheritdoc/>
    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        // Credentials are required by the SDK and ignored by dynamodb-local. They are obviously fake
        // so that nobody reads this as a place real ones could be needed — these tests must run on a
        // clean machine with no AWS account.
        Client = new AmazonDynamoDBClient(
            new BasicAWSCredentials("integration", "integration"),
            new AmazonDynamoDBConfig
            {
                ServiceURL = $"http://{_container.Hostname}:{_container.GetMappedPublicPort(DynamoDbPort)}",
                AuthenticationRegion = "eu-west-2",
            });

        await DynamoDbTables.CreateAsync(Client, CancellationToken.None);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        Client?.Dispose();

        await _container.DisposeAsync();
    }
}

/// <summary>
/// Binds <see cref="DynamoDbFixture"/> to a collection, so one container serves every test in it.
/// </summary>
[CollectionDefinition(Name)]
public sealed class DynamoDbCollectionDefinition : ICollectionFixture<DynamoDbFixture>
{
    /// <summary>
    /// The name every transaction test puts on its <c>[Collection]</c> attribute.
    /// </summary>
    public const string Name = "dynamodb";
}
