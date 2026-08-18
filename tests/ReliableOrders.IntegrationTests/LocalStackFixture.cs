using System.Text.Json;
using Amazon.Runtime;
using Amazon.SQS;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;

namespace ReliableOrders.IntegrationTests;

/// <summary>
/// One LocalStack container and one SQS client, shared by every test that needs a queue.
/// </summary>
/// <remarks>
/// <para>
/// LocalStack rather than the official Amazon emulator, because Amazon publishes none for SQS. It is
/// used for SQS and nothing else: the transaction tests stay on <c>amazon/dynamodb-local</c>, for the
/// reasons given on <see cref="DynamoDbFixture"/>, and no assertion about cancellation reasons runs
/// against anything started here.
/// </para>
/// <para>
/// <b>An auth token is required.</b> LocalStack merged its community and pro images in 2026.3.0, and
/// since 6 April 2026 the image exits with code 55 before opening its edge port unless
/// <see cref="AuthTokenVariable"/> is set. The free tier still covers everything these tests do, but
/// the token has to come from somewhere — see the SQS Emulation section of docs/testing-strategy.md
/// for how it is supplied locally and in CI, and for what it costs. The absence check below exists so
/// that a missing token is reported as a missing token, rather than as a container that would not
/// start.
/// </para>
/// <para>
/// Activation reaches api.localstack.cloud, so unlike the DynamoDB harness these tests need outbound
/// network as well as a Docker daemon. That is the licensing API, not app.localstack.cloud, which is
/// the dashboard a token is copied from — the distinction matters when an activation failure is being
/// diagnosed against a proxy's allow list.
/// </para>
/// </remarks>
public sealed class LocalStackFixture : IAsyncLifetime
{
    /// <summary>
    /// The edge port every LocalStack service is served on. The host port is assigned by Docker, so
    /// parallel runs and a developer's own container cannot collide.
    /// </summary>
    private const int EdgePort = 4566;

    /// <summary>
    /// The environment variable LocalStack activates its licence from.
    /// </summary>
    internal const string AuthTokenVariable = "LOCALSTACK_AUTH_TOKEN";

    /// <summary>
    /// An optional path to the root certificate of whatever is intercepting this machine's TLS.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Unset almost everywhere, and unset in CI. It exists for the workstation behind a corporate
    /// interceptor — Zscaler, Netskope, and the like — where licence activation is an HTTPS call to a
    /// certificate the container has no reason to trust, and the container exits 55 reporting a
    /// licensing server it cannot reach. Pointing this at the interceptor's root is what LocalStack
    /// documents as the fix, and it is read from the environment rather than committed because the
    /// certificate belongs to the network, not to this repository.
    /// </para>
    /// <para>
    /// Use the root the vendor publishes, not whatever is already installed on the host. The two are
    /// not always the same certificate: Zscaler's locally installed copy has been seen without its
    /// basic constraints marked critical, which OpenSSL 3 rejects outright — so the mount appears to
    /// do nothing and the error changes from an unknown issuer to a malformed one.
    /// </para>
    /// </remarks>
    internal const string CaBundleVariable = "LOCALSTACK_CA_BUNDLE";

    /// <summary>
    /// Where <see cref="CaBundleVariable"/> is mounted inside the container.
    /// </summary>
    private const string ContainerCaBundlePath = "/etc/ssl/certs/corp-ca.crt";

    /// <summary>
    /// The emulator image, pinned to an immutable digest.
    /// </summary>
    /// <remarks>
    /// Pinned for the same reason the DynamoDB image is, and to one more. This is a two-gigabyte pull
    /// against dynamodb-local's 758 MB, so an unpinned tag also means re-pulling that on any push to
    /// the tag. The digest is the multi-architecture index rather than a per-platform manifest, so the
    /// same reference resolves on an arm64 laptop and an amd64 runner.
    /// </remarks>
    /// <remarks>
    /// The tag is kept alongside the digest so a reader can see which version this is. Docker enforces
    /// the digest; the tag is documentation. <c>integration.yml</c> pre-pulls the same reference, and
    /// <c>ContainerImageTests</c> holds the two in step.
    /// </remarks>
    internal const string Image =
        "localstack/localstack:2026.07.4@sha256:f7b778d03717b58c3adce81a740bfafff5c6f9d639159bbf08da557c9ac1b513";

    /// <summary>
    /// The region the client authenticates against, and the one queue ARNs are composed from.
    /// </summary>
    internal const string Region = "eu-west-2";

    /// <summary>
    /// How long the container is given to become ready before the wait is called off.
    /// </summary>
    /// <remarks>
    /// See the wait strategy below for why it is bounded at all. Three minutes rather than the
    /// seconds a healthy start takes, because this is a ceiling on a pathological case and not a
    /// performance assertion — a slow runner that needed four should fail on its own merits, not on
    /// a number chosen here.
    /// </remarks>
    private static readonly TimeSpan StartupCeiling = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Why a test is skipped when this machine has no token to start a container with.
    /// </summary>
    internal const string SkipReason =
        "LOCALSTACK_AUTH_TOKEN is not set, so no SQS emulator can be started. See the SQS Emulation "
        + "section of docs/testing-strategy.md.";

    /// <summary>
    /// How many lines of each output stream a failure reports.
    /// </summary>
    private const int LoggedLines = 50;

    private IContainer? _container;

    /// <summary>
    /// Whether this machine can start the emulator at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Read by <see cref="RequiresLocalStackAttribute"/>, which is what every test using a queue is
    /// marked with, so an absent token skips those tests with a reason rather than failing them.
    /// Failing was the earlier choice, on the grounds that a skip reports green over work that never
    /// happened. That reasoning does not survive contact with the workflow: it already excludes these
    /// tests by trait and says so when no token is present, and <c>ContainerImageTests</c> holds that
    /// filter and the trait together, so CI's honesty never depended on this throwing. What the throw
    /// did cost was eight permanent failures in the IDE of anyone without a token.
    /// </para>
    /// <para>
    /// It reports whether a token exists, not whether it works. An expired or rejected one still fails
    /// the container, loudly, which is the right direction: a wrong token is a mistake and an absent
    /// one is a machine that was never set up for these tests.
    /// </para>
    /// </remarks>
    public static bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AuthTokenVariable));

    /// <summary>
    /// A client pointed at the container, usable from any test in the collection.
    /// </summary>
    public IAmazonSQS Client { get; private set; } = null!;

    /// <inheritdoc/>
    public async ValueTask InitializeAsync()
    {
        // Nothing is started when there is no token. xUnit builds a collection's fixtures whether or
        // not its tests are going to be skipped, so returning here is what keeps the skip a skip
        // rather than a fixture that throws before the first one is reported.
        if (!IsConfigured)
        {
            return;
        }

        _container = BuildContainer(ReadAuthToken());

        // Wrapped so the container's own output survives it. Every startup failure worth diagnosing
        // looks the same from out here — the wait strategy above gives up at its ceiling — and the
        // line explaining why is inside a container nothing will read before it is reaped.
        try
        {
            await _container.StartAsync();
        }
        catch (Exception failure)
        {
            throw new InvalidOperationException(await DescribeStartFailureAsync(_container), failure);
        }

        // Credentials are required by the SDK and ignored by LocalStack. They are obviously fake so
        // that nobody reads this as a place real ones could be needed — the auth token above is a
        // licence, not an AWS credential, and no test here touches an AWS account.
        Client = new AmazonSQSClient(
            new BasicAWSCredentials("integration", "integration"),
            new AmazonSQSConfig
            {
                ServiceURL = $"http://{_container.Hostname}:{_container.GetMappedPublicPort(EdgePort)}",
                AuthenticationRegion = Region,
            });
    }

    /// <summary>
    /// Provisions a source queue and its dead-letter queue, under names nothing else will use.
    /// </summary>
    /// <remarks>
    /// A fresh pair per call rather than one pair shared by the collection. A receive returns whatever
    /// is on the queue, not whatever the calling test published, so tests sharing a queue would see
    /// each other's messages and each would have to clean up after itself to keep the next one
    /// passing. Fresh queues make that impossible rather than merely discouraged, which is the same
    /// reason <see cref="OrderEvents.New"/> hands out fresh identifiers.
    /// </remarks>
    internal Task<SqsQueues> CreateQueuesAsync(CancellationToken cancellationToken) =>
        SqsQueues.CreateAsync(Client, $"t{Guid.NewGuid():N}", cancellationToken);

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        Client?.Dispose();

        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    /// <summary>
    /// What the container wrote before it failed to become ready.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An expired token, a rejected one, and a TLS interceptor <see cref="CaBundleVariable"/> does not
    /// cover all end the same way: the image exits 55 before opening its edge port, and the licensing
    /// error it printed is the diagnosis. Testcontainers does report that one itself, with the exit
    /// code and the output — what it cannot report is the container that starts, stays up, and never
    /// answers healthy, where the wait is called off at <see cref="StartupCeiling"/> and the reason is
    /// left in a log nothing reads. Both arrive here, so both say what happened.
    /// </para>
    /// <para>
    /// Reading the log can fail in its own right — an image that never pulled leaves no container to
    /// read — so that failure is reported as itself rather than replacing the one being explained.
    /// </para>
    /// </remarks>
    private static async Task<string> DescribeStartFailureAsync(IContainer container)
    {
        string written;

        try
        {
            var (stdout, stderr) = await container.GetLogsAsync(timestampsEnabled: false);

            written = $"stdout:{Environment.NewLine}{Tail(stdout)}{Environment.NewLine}{Environment.NewLine}"
                + $"stderr:{Environment.NewLine}{Tail(stderr)}";
        }
        catch (Exception unreadable)
        {
            written = $"Its logs could not be read either: {unreadable.Message}";
        }

        return "The LocalStack container did not become ready, so no SQS emulator is available. It "
            + "exits 55 before opening its edge port when its licence cannot be activated, which is "
            + $"an outbound call to api.localstack.cloud — check {AuthTokenVariable}, and "
            + $"{CaBundleVariable} on a machine behind a TLS interceptor. The last {LoggedLines} "
            + $"lines it wrote:{Environment.NewLine}{Environment.NewLine}{written}";
    }

    /// <summary>
    /// The end of an output stream, which is where a container that gave up says why.
    /// </summary>
    /// <remarks>
    /// Bounded because the ceiling is three minutes, and a container that starts, runs, and fails its
    /// health check spends all of them logging. The beginning of that is startup banner.
    /// </remarks>
    private static string Tail(string output)
    {
        var lines = output.Split(
            '\n',
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        return lines.Length == 0
            ? "(nothing)"
            : string.Join(Environment.NewLine, lines[^Math.Min(LoggedLines, lines.Length)..]);
    }

    /// <summary>
    /// Reads the auth token, which <see cref="IsConfigured"/> has already established is there.
    /// </summary>
    /// <remarks>
    /// The throw is unreachable through the fixture, which returns early when there is no token, and
    /// is kept because unreachable and impossible are different things. A future caller that reaches
    /// this without checking gets an answer naming the variable rather than a null passed on to
    /// Docker.
    /// </remarks>
    private static string ReadAuthToken() =>
        Environment.GetEnvironmentVariable(AuthTokenVariable)
        ?? throw new InvalidOperationException($"{AuthTokenVariable} is not set.");

    /// <summary>
    /// Mounts the TLS interceptor's root certificate, when this machine has one to declare.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A no-op unless <see cref="CaBundleVariable"/> is set, which is the ordinary case and the case
    /// in CI. Three variables rather than one because three clients inside the image make outbound
    /// calls and each reads its own: Python's requests, curl, and Node. Setting only the one that
    /// happens to fail first moves the failure rather than fixing it.
    /// </para>
    /// <para>
    /// A path that is set but does not exist is a mistake worth reporting. Testcontainers would
    /// otherwise create a directory at the mount source and bind an empty directory over the
    /// certificate, and the container would fail exactly as it does with no mount at all — which is
    /// the failure this variable was set to avoid.
    /// </para>
    /// </remarks>
    private static ContainerBuilder WithInterceptorCertificate(ContainerBuilder builder)
    {
        var certificate = Environment.GetEnvironmentVariable(CaBundleVariable);

        if (string.IsNullOrWhiteSpace(certificate))
        {
            return builder;
        }

        if (!File.Exists(certificate))
        {
            throw new InvalidOperationException(
                $"{CaBundleVariable} is set to '{certificate}', which does not exist. Point it at the "
                + "root certificate of this machine's TLS interceptor, or unset it.");
        }

        return builder
            .WithBindMount(Path.GetFullPath(certificate), ContainerCaBundlePath, AccessMode.ReadOnly)
            .WithEnvironment("REQUESTS_CA_BUNDLE", ContainerCaBundlePath)
            .WithEnvironment("CURL_CA_BUNDLE", ContainerCaBundlePath)
            .WithEnvironment("NODE_EXTRA_CA_CERTS", ContainerCaBundlePath);
    }

    private static IContainer BuildContainer(string authToken) =>
        WithInterceptorCertificate(new ContainerBuilder(Image))
        .WithPortBinding(EdgePort, assignRandomHostPort: true)
        .WithEnvironment(AuthTokenVariable, authToken)

        // SQS alone. The image carries every service, and loading the rest would add startup time and
        // memory to a suite that calls none of them.
        .WithEnvironment("SERVICES", "sqs")

        // Loaded at startup rather than on first call, so the readiness check below means the service
        // is ready rather than that the edge port is listening.
        .WithEnvironment("EAGER_SERVICE_LOADING", "1")

        // Queue URLs as a path on the host that was called, rather than under
        // sqs.<region>.localhost.localstack.cloud. The SDK follows the URL a CreateQueue response
        // returns, and the default strategy returns a hostname that does not resolve to the port
        // Docker mapped, so every call after the first would go nowhere.
        .WithEnvironment("SQS_ENDPOINT_STRATEGY", "path")
        .WithEnvironment("AWS_DEFAULT_REGION", Region)
        .WithEnvironment("DEBUG", "0")

        // The certificate for localhost.localstack.cloud, which the endpoint strategy above means
        // nothing here resolves and no client here presents. Skipping it removes an outbound call
        // from startup: licence activation is the one that remains, and it is the one worth naming
        // when these tests need network.
        .WithEnvironment("SKIP_SSL_CERT_DOWNLOAD", "1")

        // Usage analytics, off for the reason no AWS credential is configured anywhere in this
        // suite. What a test run does is not this container's to report.
        .WithEnvironment("DISABLE_EVENTS", "1")

        // Both conditions, because either alone is reached too early. The log line is written before
        // the edge port accepts connections, and the port accepts them before licence activation has
        // decided whether the container is going to keep running.
        //
        // Both are bounded, because the interesting failure never succeeds. An expired or invalid
        // token exits the container with code 55 and neither condition is ever met, and the
        // Testcontainers default leaves that waiting for an hour before reporting a timeout that
        // names a container start rather than a dead licence. Startup is seconds; the ceiling is
        // generous enough for a loaded runner and short enough to read as a failure.
        .WithWaitStrategy(
            Wait.ForUnixContainer()
                .UntilMessageIsLogged("Ready.", options => options.WithTimeout(StartupCeiling))
                .UntilHttpRequestIsSucceeded(
                    request => request
                        .ForPath("/_localstack/health")
                        .ForPort(EdgePort)
                        .ForResponseMessageMatching(SqsIsRunningAsync),
                    options => options.WithTimeout(StartupCeiling)))
        .Build();

    /// <summary>
    /// Whether the health response reports SQS as loaded, rather than merely known about.
    /// </summary>
    /// <remarks>
    /// The endpoint answers 200 as soon as the edge port serves, and reports a service it has not
    /// loaded yet as <c>available</c> rather than <c>running</c>. Waiting on the status code alone
    /// would clear the gate while <c>EAGER_SERVICE_LOADING</c> was still working through startup, and
    /// the first call of the first test would be what waited for it — which is the condition the
    /// comment on that variable claims this check already waits for. Reading the body is what makes
    /// that true.
    /// </remarks>
    private static async Task<bool> SqsIsRunningAsync(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            return false;
        }

        using var health = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return health.RootElement.TryGetProperty("services", out var services)
            && services.TryGetProperty("sqs", out var sqs)
            && string.Equals(sqs.GetString(), "running", StringComparison.Ordinal);
    }
}

/// <summary>
/// Binds the emulators to one collection, so a single container of each serves every test in it.
/// </summary>
/// <remarks>
/// <para>
/// Both fixtures, because <see cref="BatchResponseTests"/> spans them: it publishes to a real queue
/// and writes through the real store, which is the local end-to-end path this collection exists for.
/// A collection fixture is created per collection, so splitting the SQS tests into a collection of
/// their own would start a second LocalStack container rather than reuse this one — and at two
/// gigabytes and a licence activation each, that is the container to have one of.
/// </para>
/// <para>
/// The cost is a second <c>dynamodb-local</c> alongside the one
/// <see cref="DynamoDbCollectionDefinition"/> starts, which is seconds and a few hundred megabytes.
/// The alternative, folding the SQS fixture into that collection, would make every transaction test
/// wait on LocalStack and fail when its licence check does — coupling the tests that certify this
/// project's core mechanism to a hosted service they do not use.
/// </para>
/// </remarks>
[CollectionDefinition(Name)]
public sealed class LocalPathCollectionDefinition
    : ICollectionFixture<LocalStackFixture>, ICollectionFixture<DynamoDbFixture>
{
    /// <summary>
    /// The name every test using a queue puts on its <c>[Collection]</c> attribute.
    /// </summary>
    public const string Name = "local-path";
}
