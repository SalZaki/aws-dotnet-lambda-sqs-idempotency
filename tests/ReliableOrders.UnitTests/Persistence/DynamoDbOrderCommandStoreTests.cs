using Amazon;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using ReliableOrders.Aws.DynamoDb;
using ReliableOrders.Core.Idempotency;
using ReliableOrders.Core.Persistence;
using ReliableOrders.UnitTests.Validation;

namespace ReliableOrders.UnitTests.Persistence;

/// <summary>
/// How an SDK failure becomes a result value rather than an exception the caller has to understand.
/// </summary>
/// <remarks>
/// Classification from <c>CancellationReasons</c> is Story 2.3. What is asserted here is the mapping
/// of exception types, and that nothing escapes as an exception except cancellation.
/// </remarks>
public sealed class DynamoDbOrderCommandStoreTests
{
    private static readonly CanonicalPayloadHasher Hasher = new();

    /// <summary>
    ///
    /// </summary>
    [Fact]
    public async Task A_completed_transaction_is_created()
    {
        var result = await Act(failure: null);

        Assert.IsType<OrderWriteResult.Created>(result);
    }

    /// <summary>
    /// A reused token carrying a different body arrives before any condition is evaluated and carries
    /// no cancellation reasons, so it cannot be classified alongside the cancelled cases. Retrying it
    /// can never succeed, which is why it is permanent rather than transient.
    /// </summary>
    [Fact]
    public async Task A_token_mismatch_is_a_permanent_conflict()
    {
        var result = await Act(new IdempotentParameterMismatchException("reused"));

        var conflict = Assert.IsType<OrderWriteResult.Conflict>(result);

        Assert.Equal(ConflictScope.TokenMismatch, conflict.Scope);
        Assert.Equal(WriteFailureReason.TokenMismatch, conflict.Reason);
    }

    /// <summary>
    /// A cancelled transaction is handed to the classifier and its verdict is returned unchanged.
    /// </summary>
    /// <remarks>
    /// The classifier has its own tests over every row of the table. What this covers is the wiring
    /// between them, which nothing else in the gate does — the integration tests exercise it, but they
    /// need Docker and are excluded from the merge gate, so deleting the catch block or passing the
    /// wrong hashes would otherwise leave the fast checks green.
    /// </remarks>
    [Fact]
    public async Task A_cancelled_transaction_is_classified_rather_than_reported_raw()
    {
        var orderEvent = ValidEvent.Create();
        var hashes = Hasher.ComputeHashes(orderEvent);

        var cancellation = new TransactionCanceledException("cancelled")
        {
            CancellationReasons =
            [
                new CancellationReason
                {
                    Code = "ConditionalCheckFailed",
                    Item = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                    {
                        [IdempotencyTableSchema.EnvelopeSha256] = new() { S = hashes.EnvelopeSha256 },
                    },
                },
                new CancellationReason { Code = "None" },
            ],
        };

        var store = new DynamoDbOrderCommandStore(
            new StubDynamoDb(cancellation),
            new DynamoDbTableNames("orders", "idempotency"),
            IdempotencyRetention.Default);

        var result = await store.TryCreateAsync(orderEvent, hashes, TestContext.Current.CancellationToken);

        // The stored envelope is the one computed for this event, so the classifier must call it a
        // duplicate. A store that reported the cancellation raw would fail here.
        Assert.Equal(DuplicateScope.Event, Assert.IsType<OrderWriteResult.Duplicate>(result).Scope);
    }

    /// <summary>
    ///
    /// </summary>
    /// <param name="failure"></param>
    [Theory]
    [MemberData(nameof(TransientFailures))]
    public async Task A_service_failure_is_transient(string failure)
    {
        var result = await Act(Failure(failure));

        Assert.IsType<OrderWriteResult.TransientFault>(result);
    }

    /// <summary>
    /// A malformed request and an oversized row are defects in this service, not in any publisher.
    /// Every retry rebuilds the identical request and fails identically, so they alarm rather than
    /// spending the message's receive attempts.
    /// </summary>
    [Theory]
    [MemberData(nameof(PermanentFailures))]
    public async Task A_request_defect_is_permanent(string failure)
    {
        var result = await Act(Failure(failure));

        Assert.IsType<OrderWriteResult.PermanentFault>(result);
    }

    /// <summary>
    /// The permanent reasons name what an operator has to go and fix, so a wrong table and a missing
    /// permission do not arrive under one indistinguishable label.
    /// </summary>
    [Theory]
    [InlineData("table-not-found", WriteFailureReason.TableNotFound)]
    [InlineData("access-denied", WriteFailureReason.AccessDenied)]
    [InlineData("bad-credentials", WriteFailureReason.AccessDenied)]
    [InlineData("validation", WriteFailureReason.MalformedRequest)]
    [InlineData("item-size", WriteFailureReason.ItemTooLarge)]
    public async Task A_permanent_failure_names_what_has_to_be_fixed(string failure, string expected)
    {
        var result = await Act(Failure(failure));

        Assert.Equal(expected, Assert.IsType<OrderWriteResult.PermanentFault>(result).Reason);
    }

    /// <summary>
    /// Cancellation the SDK wrapped on its way out is unwrapped rather than reported as a downstream
    /// fault.
    /// </summary>
    /// <remarks>
    /// The unwrapped case is what the direct test below cannot reach. A caller looking for
    /// <see cref="OperationCanceledException"/> would otherwise miss it inside a wrapper and this
    /// service's own deadline would be logged against DynamoDB.
    /// </remarks>
    [Fact]
    public async Task Wrapped_cancellation_is_unwrapped_rather_than_reported_as_transient()
    {
        var wrapped = new AmazonDynamoDBException("cancelled", new OperationCanceledException());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Act(wrapped));
    }

    /// <summary>
    /// The caller's token reaches the SDK, rather than the call running to completion regardless.
    /// </summary>
    /// <remarks>
    /// The other cancellation tests prove the store does not reclassify a cancellation once it happens.
    /// Neither of them would notice a store that dropped the token on the floor, because a cancellation
    /// would then never be raised at all — the invocation would keep working past its deadline instead.
    /// </remarks>
    [Fact]
    public async Task The_cancellation_token_is_forwarded_to_the_sdk()
    {
        using var cancellation = new CancellationTokenSource();
        var client = new StubDynamoDb(null);
        var orderEvent = ValidEvent.Create();

        var store = new DynamoDbOrderCommandStore(
            client,
            new DynamoDbTableNames("orders", "idempotency"),
            IdempotencyRetention.Default);

        await store.TryCreateAsync(orderEvent, Hasher.ComputeHashes(orderEvent), cancellation.Token);

        Assert.Equal(cancellation.Token, client.ObservedToken);
    }

    /// <summary>
    /// Cancellation is not a downstream fault. The invocation is ending, and reporting it as one would
    /// blame DynamoDB for this service's own deadline, so it propagates rather than being reclassified.
    /// </summary>
    [Fact]
    public async Task Cancellation_propagates_rather_than_becoming_a_transient_fault()
    {
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Act(new OperationCanceledException()));
    }

    /// <summary>
    ///
    /// </summary>
    [Fact]
    public void A_null_dependency_throws()
    {
        var tables = new DynamoDbTableNames("orders", "idempotency");

        Assert.Throws<ArgumentNullException>(
            () => new DynamoDbOrderCommandStore(null!, tables, IdempotencyRetention.Default));

        Assert.Throws<ArgumentNullException>(
            () => new DynamoDbOrderCommandStore(new StubDynamoDb(null), null!, IdempotencyRetention.Default));

        Assert.Throws<ArgumentNullException>(
            () => new DynamoDbOrderCommandStore(new StubDynamoDb(null), tables, null!));
    }

    /// <summary>
    ///
    /// </summary>
    /// <returns></returns>
    public static TheoryData<string> TransientFailures() =>
        ["throughput", "request-limit", "service", "unknown-code"];

    /// <remarks>
    /// A wrong table name and a missing permission are deployment defects, not downstream faults.
    /// Reported as transient they would spend every message's receive attempts and dead-letter the lot,
    /// under an alarm blaming DynamoDB for a typo in an environment variable.
    /// </remarks>
    public static TheoryData<string> PermanentFailures() =>
        ["item-size", "validation", "table-not-found", "access-denied", "bad-credentials"];

    private static AmazonDynamoDBException Failure(string name) => name switch
    {
        "throughput" => new ProvisionedThroughputExceededException("slow down"),
        "request-limit" => new RequestLimitExceededException("slow down"),
        "service" => new AmazonDynamoDBException("unavailable"),
        // An error code the mapping does not list stays transient and is retried, which is the safe
        // direction when the cause is unknown.
        "unknown-code" => new AmazonDynamoDBException("odd") { ErrorCode = "SomethingNewException" },
        "item-size" => new ItemCollectionSizeLimitExceededException("too large"),
        "validation" => new AmazonDynamoDBException("bad request") { ErrorCode = "ValidationException" },
        "table-not-found" => new ResourceNotFoundException("no such table"),
        "access-denied" => new AmazonDynamoDBException("denied") { ErrorCode = "AccessDeniedException" },
        "bad-credentials" => new AmazonDynamoDBException("who?") { ErrorCode = "UnrecognizedClientException" },
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "No failure defined."),
    };

    private static async Task<OrderWriteResult> Act(Exception? failure)
    {
        var orderEvent = ValidEvent.Create();

        var store = new DynamoDbOrderCommandStore(
            new StubDynamoDb(failure),
            new DynamoDbTableNames("orders", "idempotency"),
            IdempotencyRetention.Default);

        return await store.TryCreateAsync(
            orderEvent,
            Hasher.ComputeHashes(orderEvent),
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// A client that fails the way the test asks, or succeeds.
    /// </summary>
    /// <remarks>
    /// Derived from the real client and overriding the one virtual method under test, rather than
    /// hand-implementing the whole of <see cref="IAmazonDynamoDB"/>. It never opens a connection,
    /// because every call the store makes is intercepted here.
    /// </remarks>
    private sealed class StubDynamoDb(Exception? failure) : AmazonDynamoDBClient(
        new BasicAWSCredentials("stub", "stub"),
        new AmazonDynamoDBConfig { RegionEndpoint = RegionEndpoint.EUWest2 })
    {
        /// <summary>
        /// The token the store handed to the SDK, so a test can assert it was the caller's own.
        /// </summary>
        public CancellationToken ObservedToken { get; private set; }

        public override Task<TransactWriteItemsResponse> TransactWriteItemsAsync(
            TransactWriteItemsRequest request,
            CancellationToken cancellationToken = default)
        {
            ObservedToken = cancellationToken;

            return failure is null
                ? Task.FromResult(new TransactWriteItemsResponse())
                : Task.FromException<TransactWriteItemsResponse>(failure);
        }
    }
}
