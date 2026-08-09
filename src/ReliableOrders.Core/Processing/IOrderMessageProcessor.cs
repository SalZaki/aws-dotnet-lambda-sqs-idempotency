using ReliableOrders.Core.Observability;

namespace ReliableOrders.Core.Processing;

/// <summary>
/// Processes one message, end to end, and says what happened.
/// </summary>
/// <remarks>
/// <para>
/// One message, not a batch. Deciding what a batch response contains is the handler's job, and
/// keeping the two apart is what lets every outcome here be tested without a transport.
/// </para>
/// <para>
/// Specification v2 gave this method a <c>ProcessingContext</c> of the Lambda request identifier,
/// the service name and the environment. None of the three survives: Story 5.1 moved the service and
/// environment onto <c>ProcessingLog</c>, which owns them for the process, and put the request
/// identifier on the invocation scope the batch handler opens — so a processor that took them would
/// be a second source for values it never reads. What is left is the invocation's metrics, which is
/// a collaborator rather than context, so it is passed as one.
/// </para>
/// </remarks>
public interface IOrderMessageProcessor
{
    /// <summary>
    /// Parses, validates, hashes and persists one message.
    /// </summary>
    /// <remarks>
    /// Does not throw for a message it can classify, however malformed. What does propagate is
    /// cancellation, which means the invocation is ending rather than that anything failed — see
    /// <see cref="Persistence.IOrderCommandStore"/>, which draws the same line.
    /// </remarks>
    /// <param name="message">The message to process.</param>
    /// <param name="metrics">
    /// The invocation's metrics. Passed in rather than held, because an accumulator belongs to one
    /// invocation while this processor is built once per execution environment.
    /// </param>
    /// <param name="cancellationToken">Forwarded to the store.</param>
    /// <returns>What happened, never null.</returns>
    Task<MessageProcessingResult> ProcessAsync(
        IncomingMessage message,
        IInvocationMetrics metrics,
        CancellationToken cancellationToken);
}
