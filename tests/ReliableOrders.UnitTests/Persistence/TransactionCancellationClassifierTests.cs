using Amazon.DynamoDBv2.Model;
using ReliableOrders.Aws.DynamoDb;
using ReliableOrders.Core.Idempotency;
using ReliableOrders.Core.Persistence;

namespace ReliableOrders.UnitTests.Persistence;

/// <summary>
/// Every row of the Duplicate and Conflict Classification table in docs/correctness-model.md.
/// </summary>
public sealed class TransactionCancellationClassifierTests
{
    private const string EnvelopeHash = "envelope-hash";
    private const string BusinessHash = "business-hash";

    private static readonly PayloadHashes Computed = new(EnvelopeHash, BusinessHash);

    /// <summary>
    /// The same event delivered twice. The stored envelope matches, so the first delivery already
    /// wrote both rows and this one is acknowledged without writing anything.
    /// </summary>
    [Fact]
    public void A_repeated_event_with_a_matching_envelope_is_an_event_duplicate()
    {
        var result = Classify(
            claim: Failed(IdempotencyTableSchema.EnvelopeSha256, EnvelopeHash),
            order: Unaffected());

        Assert.Equal(DuplicateScope.Event, Assert.IsType<OrderWriteResult.Duplicate>(result).Scope);
    }

    /// <summary>
    /// One event identifier used for two different events. No retry settles it, so it is permanent and
    /// a person has to look at the publisher.
    /// </summary>
    [Fact]
    public void A_repeated_event_with_a_different_envelope_is_an_event_conflict()
    {
        var result = Classify(
            claim: Failed(IdempotencyTableSchema.EnvelopeSha256, "something-else"),
            order: Unaffected());

        var conflict = Assert.IsType<OrderWriteResult.Conflict>(result);

        Assert.Equal(ConflictScope.Event, conflict.Scope);
        Assert.Equal(WriteFailureReason.EnvelopeHashMismatch, conflict.Reason);
    }

    /// <summary>
    /// The reason two hashes exist. A republish carries a new event identifier and a later timestamp,
    /// so only the business hash can tell it from genuine divergence — and getting this wrong
    /// dead-letters a valid order with a high-severity alarm.
    /// </summary>
    [Fact]
    public void A_republished_order_with_matching_data_is_an_order_duplicate()
    {
        var result = Classify(
            claim: Unaffected(),
            order: Failed(OrderTableSchema.BusinessSha256, BusinessHash));

        Assert.Equal(DuplicateScope.Order, Assert.IsType<OrderWriteResult.Duplicate>(result).Scope);
    }

    [Fact]
    public void An_existing_order_with_different_data_is_an_order_conflict()
    {
        var result = Classify(
            claim: Unaffected(),
            order: Failed(OrderTableSchema.BusinessSha256, "diverged"));

        var conflict = Assert.IsType<OrderWriteResult.Conflict>(result);

        Assert.Equal(ConflictScope.Order, conflict.Scope);
        Assert.Equal(WriteFailureReason.BusinessHashMismatch, conflict.Reason);
    }

    /// <summary>
    /// The event-level check wins. Both conditions failed, and the envelope differs, so this is a
    /// conflict whatever the order row holds — the order row is not consulted at all.
    /// </summary>
    [Fact]
    public void When_both_conditions_fail_the_envelope_decides()
    {
        var result = Classify(
            claim: Failed(IdempotencyTableSchema.EnvelopeSha256, "something-else"),
            order: Failed(OrderTableSchema.BusinessSha256, BusinessHash));

        Assert.Equal(ConflictScope.Event, Assert.IsType<OrderWriteResult.Conflict>(result).Scope);
    }

    /// <summary>
    /// A returned item is the only evidence available, so its absence is never read as agreement. The
    /// row was most plausibly removed by TTL expiry between the condition and the response.
    /// </summary>
    [Theory]
    [MemberData(nameof(UnusableItems))]
    public void A_condition_failure_with_no_usable_item_is_transient(string shape)
    {
        var result = Classify(claim: Unusable(shape), order: Unaffected());

        Assert.Equal(
            WriteFailureReason.ConflictingItemMissing,
            Assert.IsType<OrderWriteResult.TransientFault>(result).Reason);
    }

    [Theory]
    [InlineData("TransactionConflict", WriteFailureReason.TransactionConflict)]
    [InlineData("ThrottlingError", WriteFailureReason.Throttled)]
    [InlineData("ProvisionedThroughputExceeded", WriteFailureReason.Throttled)]
    public void A_contended_or_throttled_transaction_is_transient(string code, string expected)
    {
        var result = Classify(claim: WithCode(code), order: Unaffected());

        Assert.Equal(expected, Assert.IsType<OrderWriteResult.TransientFault>(result).Reason);
    }

    /// <summary>
    /// Defects in this service rather than anything about the message. Every retry rebuilds the same
    /// request and fails identically, so they alarm rather than spending the receive attempts.
    /// </summary>
    [Theory]
    [InlineData("ValidationError", WriteFailureReason.MalformedRequest)]
    [InlineData("ItemCollectionSizeLimitExceeded", WriteFailureReason.ItemTooLarge)]
    public void A_request_defect_is_permanent(string code, string expected)
    {
        var result = Classify(claim: Unaffected(), order: WithCode(code));

        Assert.Equal(expected, Assert.IsType<OrderWriteResult.PermanentFault>(result).Reason);
    }

    /// <summary>
    /// A reason code at either index is enough, so a failure at the order put is not missed because
    /// the claim succeeded.
    /// </summary>
    [Fact]
    public void A_reason_code_at_either_index_is_classified()
    {
        Assert.IsType<OrderWriteResult.TransientFault>(
            Classify(claim: WithCode("ThrottlingError"), order: Unaffected()));

        Assert.IsType<OrderWriteResult.TransientFault>(
            Classify(claim: Unaffected(), order: WithCode("ThrottlingError")));
    }

    /// <summary>
    /// An unrecognised code stays transient and is retried, which is the safe direction when the cause
    /// is unknown. Acknowledging it would risk dropping a message whose order was never stored.
    /// </summary>
    [Fact]
    public void An_unrecognised_reason_code_is_transient()
    {
        var result = Classify(claim: WithCode("SomethingNew"), order: Unaffected());

        Assert.IsType<OrderWriteResult.TransientFault>(result);
    }

    /// <summary>
    /// A response carrying anything other than one reason per item cannot be read by the rules above,
    /// so it is retried rather than guessed at.
    /// </summary>
    /// <remarks>
    /// Reported under its own reason rather than as a missing conflicting item. One means a row expired
    /// between the condition and the response, which is ordinary; this means the response cannot be
    /// interpreted, which is not, and an operator has to be able to tell them apart.
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void A_response_of_the_wrong_shape_is_transient(int reasonCount)
    {
        var cancellation = new TransactionCanceledException("cancelled")
        {
            CancellationReasons = [.. Enumerable.Range(0, reasonCount).Select(_ => Unaffected())],
        };

        Assert.Equal(
            WriteFailureReason.UnreadableCancellation,
            Assert.IsType<OrderWriteResult.TransientFault>(
                TransactionCancellationClassifier.Classify(cancellation, Computed)).Reason);
    }

    /// <summary>
    /// No client is reachable through the classifier's signature or its state.
    /// </summary>
    /// <remarks>
    /// Named for what it checks rather than for the guarantee it supports. It inspects parameters and
    /// fields, so a method body constructing a client inside itself would pass — the protection against
    /// the follow-up read is that reintroducing it means widening this signature, which is a visible
    /// change in review, not something this assertion can detect on its own.
    /// </remarks>
    [Fact]
    public void The_classifier_takes_no_dynamodb_client()
    {
        var parameterTypes = typeof(TransactionCancellationClassifier)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .SelectMany(method => method.GetParameters())
            .Select(parameter => parameter.ParameterType.Name);

        Assert.DoesNotContain("IAmazonDynamoDB", parameterTypes);

        Assert.DoesNotContain(
            typeof(TransactionCancellationClassifier).GetFields(
                System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.Static
                | System.Reflection.BindingFlags.Instance),
            field => field.FieldType.Name.Contains("DynamoDB", StringComparison.Ordinal));
    }

    [Fact]
    public void A_null_argument_throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => TransactionCancellationClassifier.Classify(null!, Computed));

        Assert.Throws<ArgumentNullException>(
            () => TransactionCancellationClassifier.Classify(new TransactionCanceledException("x"), null!));
    }

    public static TheoryData<string> UnusableItems() => ["null", "empty", "missing-attribute"];

    private static OrderWriteResult Classify(CancellationReason claim, CancellationReason order) =>
        TransactionCancellationClassifier.Classify(
            new TransactionCanceledException("cancelled") { CancellationReasons = [claim, order] },
            Computed);

    private static CancellationReason Failed(string attributeName, string storedHash) => new()
    {
        Code = "ConditionalCheckFailed",
        Item = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
        {
            [attributeName] = new() { S = storedHash },
        },
    };

    private static CancellationReason Unusable(string shape) => shape switch
    {
        "null" => new CancellationReason { Code = "ConditionalCheckFailed", Item = null },
        "empty" => new CancellationReason
        {
            Code = "ConditionalCheckFailed",
            Item = new Dictionary<string, AttributeValue>(StringComparer.Ordinal),
        },
        "missing-attribute" => Failed("SomeOtherAttribute", "irrelevant"),
        _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "No shape defined."),
    };

    private static CancellationReason Unaffected() => new() { Code = "None" };

    private static CancellationReason WithCode(string code) => new() { Code = code };
}
