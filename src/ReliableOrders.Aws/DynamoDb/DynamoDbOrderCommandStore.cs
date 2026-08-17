using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using ReliableOrders.Core.Contracts;
using ReliableOrders.Core.Idempotency;
using ReliableOrders.Core.Observability;
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

        // A client span covering the transaction and whatever the SDK instrumentation records inside
        // it. No outcome is written here: every branch below returns a result the processor already
        // puts on the record's span, and duplicating it would leave two places to change when the
        // vocabulary does. What this span adds is the boundary — how long the write took, and that it
        // was a call out of the process rather than work done in it.
        using var span = Tracing.Source.StartActivity(Tracing.Spans.Persist, ActivityKind.Client);

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
            // The write is over, so its span ends before classification begins. Left running, the
            // persist span would enclose the classify span and its duration would include the
            // classifier's — so a latency alarm on persist would fire on classifier slowness, on
            // exactly the conflict path where the two most need telling apart. Disposal is idempotent,
            // so the using below still runs and still does the right thing.
            span?.Dispose();

            // Classified from the reasons the response already carries. No follow-up read: it would
            // cost a round trip on the commonest retry path and let the row change in between.
            return Classify(cancellation, hashes);
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
        catch (AmazonServiceException exception)
            when (exception.InnerException is OperationCanceledException inner
                && cancellationToken.IsCancellationRequested)
        {
            // Cancellation that the SDK wrapped on its way out. Unwrapped rather than reported as a
            // downstream fault, because the invocation is ending and blaming DynamoDB for this
            // service's own deadline would send an operator looking in the wrong place. Captured so the
            // original stack survives the rethrow.
            //
            // The token check is what separates that from a client-side HTTP timeout. TaskCanceledException
            // derives from OperationCanceledException, and ClientConfig.Timeout raises one with nobody
            // having cancelled anything — an ordinary transient fault this store's contract says it
            // reports by returning a value. Without the check it left here as an exception instead, and
            // the handler caught it in the branch reserved for defects, which logs the record without
            // the order identity the processor had already put in scope. The outcome was right and the
            // line was unfindable by event or order.
            ExceptionDispatchInfo.Capture(inner).Throw();

            throw;
        }
        catch (AmazonServiceException)
        {
            return new OrderWriteResult.TransientFault(WriteFailureReason.ServiceUnavailable);
        }
    }

    /// <summary>
    /// Classifies a cancelled transaction, under a span of its own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Separated from the persist span because the two answer different questions. Persist says how
    /// long DynamoDB took; this says how long was spent deciding what its refusal meant, and it is the
    /// step whose correctness this whole project is about. A conflict investigated months from now is
    /// findable by this span and the scope it carries.
    /// </para>
    /// <para>
    /// The scope is the only attribute, and it is absent for the outcomes that have none. Nothing here
    /// records the stored item or the hash that disagreed: a conflict is diagnosed from the computed
    /// hash on the log line, which is redacted for that purpose, and copying it onto a span would put
    /// it in a second system under different retention.
    /// </para>
    /// </remarks>
    private static OrderWriteResult Classify(TransactionCanceledException cancellation, PayloadHashes hashes)
    {
        using var span = Tracing.Source.StartActivity(Tracing.Spans.Classify);

        var result = TransactionCancellationClassifier.Classify(cancellation, hashes);

        span?.SetTag(
            Tracing.Attributes.Scope,
            result.Match(
                whenCreated: _ => (string?)null,
                whenDuplicate: duplicate => duplicate.Scope.ToString(),
                whenConflict: conflict => conflict.Scope.ToString(),
                whenTransientFault: _ => null,
                whenPermanentFault: _ => null));

        return result;
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
