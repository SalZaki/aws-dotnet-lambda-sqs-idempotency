using Amazon.Lambda.SQSEvents;

namespace ReliableOrders.Aws.Sqs;

/// <summary>
/// Builds the failure list a batch response may contain, and refuses anything it may not.
/// </summary>
/// <remarks>
/// <para>
/// A separate type because of what one bad entry costs. Lambda reprocesses the <b>entire batch</b>
/// when the failure list carries an identifier it does not recognise, so a null, a blank or a
/// duplicate turns a one-record failure into a ten-record replay — and nothing reports it. The
/// symptom is a queue that keeps redelivering messages the logs say were processed.
/// </para>
/// <para>
/// Every route to a batch response goes through <see cref="From"/>, so the rule is applied once
/// rather than remembered at each call site. It filters rather than throws: a handler that threw
/// here would abandon the results of every other record in the batch to protect the response's
/// shape, which is the trade the wrong way round.
/// </para>
/// </remarks>
public static class BatchItemFailures
{
    /// <summary>
    /// Builds a response from the identifiers of the records that failed.
    /// </summary>
    /// <remarks>
    /// Order is preserved so a response reads in the order the batch was processed, which is what
    /// makes it comparable with the log. Duplicates keep their first position.
    /// </remarks>
    /// <param name="failedMessageIds">
    /// SQS message identifiers, never domain event identifiers. Lambda matches these against the
    /// records it delivered, and an event identifier matches none of them.
    /// </param>
    /// <returns>A response Lambda can act on, with an empty list when nothing failed.</returns>
    public static SQSBatchResponse From(IEnumerable<string?> failedMessageIds)
    {
        ArgumentNullException.ThrowIfNull(failedMessageIds);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var failures = new List<SQSBatchResponse.BatchItemFailure>();

        foreach (var messageId in failedMessageIds)
        {
            if (string.IsNullOrWhiteSpace(messageId) || !seen.Add(messageId))
            {
                continue;
            }

            failures.Add(new SQSBatchResponse.BatchItemFailure { ItemIdentifier = messageId });
        }

        return new SQSBatchResponse { BatchItemFailures = failures };
    }
}
