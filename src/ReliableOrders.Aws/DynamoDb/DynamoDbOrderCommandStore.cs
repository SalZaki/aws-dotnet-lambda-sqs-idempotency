using System.Runtime.ExceptionServices;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using ReliableOrders.Core.Contracts;
using ReliableOrders.Core.Idempotency;
using ReliableOrders.Core.Persistence;

namespace ReliableOrders.Aws.DynamoDb;

/// <inheritdoc cref="IOrderCommandStore"/>
/// <remarks>
/// <para>
/// Both rows are written by one <c>TransactWriteItems</c> call. There is no method here that writes
/// the idempotency row on its own, and that is the point rather than an omission — claiming an event
/// and then stopping before the order is stored loses the order silently, because the retry sees the
/// claim and skips the message.
/// </para>
/// <para>
/// A cancelled transaction is classified by <see cref="TransactionCancellationClassifier"/> from the
/// reasons the response already carries. Nothing is read back afterwards.
/// </para>
/// </remarks>
public sealed class DynamoDbOrderCommandStore : IOrderCommandStore
{
    private readonly IAmazonDynamoDB _client;
    private readonly DynamoDbTableNames _tables;
    private readonly IdempotencyRetention _retention;

    /// <param name="client">
    /// Reused across invocations. Constructing one per message would pay connection and credential
    /// setup on every record.
    /// </param>
    /// <param name="tables">Where the two rows are written.</param>
    /// <param name="retention">How long an idempotency row is kept.</param>
    public DynamoDbOrderCommandStore(
        IAmazonDynamoDB client,
        DynamoDbTableNames tables,
        IdempotencyRetention retention)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentNullException.ThrowIfNull(retention);

        _client = client;
        _tables = tables;
        _retention = retention;
    }

    /// <inheritdoc/>
    public async Task<OrderWriteResult> TryCreateAsync(
        OrderCreatedV1 message,
        PayloadHashes hashes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(hashes);

        var request = OrderTransactionFactory.Create(
            new OrderWriteRequest(message, hashes, _retention),
            _tables);

        try
        {
            await _client.TransactWriteItemsAsync(request, cancellationToken);

            return new OrderWriteResult.Created();
        }
        catch (IdempotentParameterMismatchException)
        {
            // The token is the event identifier and the body is a pure function of the event, so this
            // can only mean one identifier carried two different payloads. It arrives before any
            // condition is evaluated and carries no cancellation reasons at all, which is why it needs
            // its own branch rather than falling through to the one below.
            return new OrderWriteResult.Conflict(ConflictScope.TokenMismatch, WriteFailureReason.TokenMismatch);
        }
        catch (TransactionCanceledException cancellation)
        {
            // Classified from the reasons the response already carries. No follow-up read: it would
            // cost a round trip on the commonest retry path and let the row change in between.
            return TransactionCancellationClassifier.Classify(cancellation, hashes);
        }
        catch (ItemCollectionSizeLimitExceededException)
        {
            // The contract's field limits keep the worst-case row far below the 400 KB ceiling, so
            // reaching this means a limit was raised without recalculating. Every retry fails the same.
            return new OrderWriteResult.PermanentFault(WriteFailureReason.ItemTooLarge);
        }
        catch (ResourceNotFoundException)
        {
            // A wrong or missing table name. Deployment, not downstream — retrying looks for the same
            // absent table until the message dead-letters, under an alarm blaming DynamoDB.
            return new OrderWriteResult.PermanentFault(WriteFailureReason.TableNotFound);
        }
        catch (ProvisionedThroughputExceededException)
        {
            return new OrderWriteResult.TransientFault(WriteFailureReason.Throttled);
        }
        catch (RequestLimitExceededException)
        {
            return new OrderWriteResult.TransientFault(WriteFailureReason.Throttled);
        }
        catch (AmazonDynamoDBException exception) when (PermanentReasonFor(exception) is not null)
        {
            // Defects in how this service is built or deployed rather than anything about the message.
            // Every retry produces the identical request and fails identically, so they alarm instead
            // of spending the message's receive attempts.
            return new OrderWriteResult.PermanentFault(PermanentReasonFor(exception)!);
        }
        catch (AmazonServiceException exception) when (exception.InnerException is OperationCanceledException inner)
        {
            // Cancellation that the SDK wrapped on its way out. Unwrapped rather than reported as a
            // downstream fault, because the invocation is ending and blaming DynamoDB for this
            // service's own deadline would send an operator looking in the wrong place. Captured so the
            // original stack survives the rethrow.
            ExceptionDispatchInfo.Capture(inner).Throw();

            throw;
        }
        catch (AmazonServiceException)
        {
            return new OrderWriteResult.TransientFault(WriteFailureReason.ServiceUnavailable);
        }
    }

    /// <summary>
    /// The reason a failure is permanent, or null when it is not one of them.
    /// </summary>
    /// <remarks>
    /// Matched on the error code because the SDK models none of these as its own exception type on
    /// DynamoDB. An unrecognised code is deliberately absent, so anything unlisted stays transient and
    /// is retried — the safe direction when the cause is unknown.
    /// <para>
    /// <c>OperationCanceledException</c> is caught nowhere except to unwrap it. The invocation ending
    /// is not a downstream fault.
    /// </para>
    /// </remarks>
    private static string? PermanentReasonFor(AmazonDynamoDBException exception) => exception.ErrorCode switch
    {
        ValidationErrorCode => WriteFailureReason.MalformedRequest,
        AccessDeniedErrorCode or UnrecognizedClientErrorCode => WriteFailureReason.AccessDenied,
        _ => null,
    };

    /// <summary>The request was malformed, which is a defect in how it is built.</summary>
    private const string ValidationErrorCode = "ValidationException";

    /// <summary>The execution role lacks an action the transaction needs.</summary>
    private const string AccessDeniedErrorCode = "AccessDeniedException";

    /// <summary>The credentials were rejected, which no retry resolves.</summary>
    private const string UnrecognizedClientErrorCode = "UnrecognizedClientException";
}
