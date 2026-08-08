using Amazon.DynamoDBv2.Model;
using ReliableOrders.Core.Idempotency;
using ReliableOrders.Core.Persistence;

namespace ReliableOrders.Aws.DynamoDb;

/// <summary>
/// Decides what a cancelled transaction means, from the cancellation reasons alone.
/// </summary>
/// <remarks>
/// <para>
/// Takes no DynamoDB client, and that is the design rather than an accident. Specification v1 read the
/// conflicting row back after a cancellation, which cost a round trip on the commonest retry path and
/// opened a window in which the row could change between the cancellation and the read. Because
/// nothing here can issue a request, no future change can quietly reintroduce that read without first
/// widening this signature.
/// </para>
/// <para>
/// The reasons are positionally aligned with the request's items, so index 0 is the idempotency put
/// and index 1 the order put. <c>OrderTransactionFactory</c> builds them in that order and
/// <c>DynamoDbHarnessTests</c> holds DynamoDB to reporting them that way.
/// </para>
/// </remarks>
public static class TransactionCancellationClassifier
{
    /// <summary>The condition on the partition key refused the write.</summary>
    private const string ConditionalCheckFailed = "ConditionalCheckFailed";

    /// <summary>This item did not cause the cancellation.</summary>
    private const string NoReason = "None";

    /// <summary>Another transaction touched one of the same items.</summary>
    private const string TransactionConflictCode = "TransactionConflict";

    /// <summary>The table rejected the request for capacity reasons.</summary>
    private const string ThrottlingCode = "ThrottlingError";

    /// <inheritdoc cref="ThrottlingCode"/>
    private const string ProvisionedThroughputExceededCode = "ProvisionedThroughputExceeded";

    /// <summary>The request was malformed, which is a defect in how it is built.</summary>
    private const string ValidationCode = "ValidationError";

    /// <summary>The row exceeded an item or collection size limit.</summary>
    private const string ItemTooLargeCode = "ItemCollectionSizeLimitExceeded";

    /// <summary>
    /// Classifies a cancelled transaction.
    /// </summary>
    /// <param name="cancellation">The exception DynamoDB raised.</param>
    /// <param name="hashes">The hashes computed for the event being written.</param>
    /// <returns>What the cancellation means, never null.</returns>
    public static OrderWriteResult Classify(TransactionCanceledException cancellation, PayloadHashes hashes)
    {
        ArgumentNullException.ThrowIfNull(cancellation);
        ArgumentNullException.ThrowIfNull(hashes);

        var reasons = cancellation.CancellationReasons;

        // Fewer reasons than items means the response is not the shape every rule below reads. Retried
        // rather than guessed at, because inferring a duplicate from a response that cannot be
        // interpreted would acknowledge a message whose order may never have been stored.
        if (reasons is not { Count: 2 })
        {
            return new OrderWriteResult.TransientFault(WriteFailureReason.UnreadableCancellation);
        }

        var claim = reasons[0];
        var order = reasons[1];

        // The event-level check wins. A repeat of the same event identifier carrying a different
        // envelope is a conflict whatever the order row holds, so the envelope is compared first and
        // index 1 is not consulted at all.
        if (IsConditionalCheckFailed(claim))
        {
            return Compare(
                claim,
                IdempotencyTableSchema.EnvelopeSha256,
                hashes.EnvelopeSha256,
                DuplicateScope.Event,
                ConflictScope.Event,
                WriteFailureReason.EnvelopeHashMismatch);
        }

        // The claim succeeded and the order already exists. This is the republish path — a new event
        // identifier describing an order that was already stored — and the business hash is the only
        // thing that can tell a benign republish from genuine divergence.
        if (IsUnaffected(claim) && IsConditionalCheckFailed(order))
        {
            return Compare(
                order,
                OrderTableSchema.BusinessSha256,
                hashes.BusinessSha256,
                DuplicateScope.Order,
                ConflictScope.Order,
                WriteFailureReason.BusinessHashMismatch);
        }

        return ClassifyByReasonCode(claim, order);
    }

    /// <remarks>
    /// A returned item is the only evidence available, so its absence is never read as agreement. The
    /// row was most plausibly removed by TTL expiry between the condition being evaluated and the
    /// response being built. Retrying re-evaluates against whatever is actually there.
    /// </remarks>
    private static OrderWriteResult Compare(
        CancellationReason reason,
        string attributeName,
        string computedHash,
        DuplicateScope duplicateScope,
        ConflictScope conflictScope,
        string conflictReason)
    {
        if (reason.Item is not { Count: > 0 }
            || !reason.Item.TryGetValue(attributeName, out var stored)
            || string.IsNullOrEmpty(stored.S))
        {
            return new OrderWriteResult.TransientFault(WriteFailureReason.ConflictingItemMissing);
        }

        return string.Equals(stored.S, computedHash, StringComparison.Ordinal)
            ? new OrderWriteResult.Duplicate(duplicateScope)
            : new OrderWriteResult.Conflict(conflictScope, conflictReason);
    }

    /// <remarks>
    /// Reached only when neither condition refused the write, so the cancellation came from something
    /// else. Permanent codes are checked before transient ones: a request DynamoDB will not accept
    /// fails identically on every retry, and reporting it as transient would spend the message's
    /// receive attempts before dead-lettering it under the wrong alarm.
    /// </remarks>
    private static OrderWriteResult ClassifyByReasonCode(CancellationReason claim, CancellationReason order)
    {
        var permanentReason = PermanentReason(claim) ?? PermanentReason(order);

        if (permanentReason is not null)
        {
            return new OrderWriteResult.PermanentFault(permanentReason);
        }

        var transientReason = TransientReason(claim) ?? TransientReason(order);

        if (transientReason is not null)
        {
            return new OrderWriteResult.TransientFault(transientReason);
        }

        // An unrecognised code stays transient and is retried, which is the safe direction when the
        // cause is unknown.
        return new OrderWriteResult.TransientFault(WriteFailureReason.ServiceUnavailable);
    }

    private static string? PermanentReason(CancellationReason reason) => reason.Code switch
    {
        ValidationCode => WriteFailureReason.MalformedRequest,
        ItemTooLargeCode => WriteFailureReason.ItemTooLarge,
        _ => null,
    };

    private static string? TransientReason(CancellationReason reason) => reason.Code switch
    {
        TransactionConflictCode => WriteFailureReason.TransactionConflict,
        ThrottlingCode or ProvisionedThroughputExceededCode => WriteFailureReason.Throttled,
        _ => null,
    };

    private static bool IsConditionalCheckFailed(CancellationReason reason) =>
        string.Equals(reason.Code, ConditionalCheckFailed, StringComparison.Ordinal);

    private static bool IsUnaffected(CancellationReason reason) =>
        string.Equals(reason.Code, NoReason, StringComparison.Ordinal);
}
