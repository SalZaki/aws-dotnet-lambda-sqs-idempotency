namespace ReliableOrders.Aws.Sqs;

/// <summary>
/// What one invocation of the handler needs to know about itself.
/// </summary>
/// <remarks>
/// Takes an instant rather than an <c>ILambdaContext</c>. The handler needs one fact from that
/// interface — when to stop — and depending on the whole thing would make every test build a Lambda
/// context to assert on a batch. Turning remaining time into a deadline is the composition root's
/// job; see <see cref="ProcessingDeadline"/>.
/// </remarks>
/// <param name="LambdaRequestId">
/// The invocation's request identifier, which every line the batch writes is scoped by.
/// </param>
/// <param name="Deadline">
/// The instant after which no further record is attempted. Already includes the safety margin — see
/// <see cref="ProcessingDeadline.From"/> for how it is sized and why.
/// </param>
public sealed record BatchInvocation(string LambdaRequestId, DateTimeOffset Deadline);
