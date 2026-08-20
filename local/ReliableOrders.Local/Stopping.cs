namespace ReliableOrders.Local;

/// <summary>
/// Tells a deliberate stop apart from an operation that gave up.
/// </summary>
/// <remarks>
/// <para>
/// The two are the same type, and that is the whole reason this exists. A stop signal cancels the
/// token and the call in flight throws <see cref="OperationCanceledException"/>; an
/// <see cref="HttpClient"/> whose timeout elapses throws <c>TaskCanceledException</c>, which derives
/// from it, and the AWS SDK can do the same. Catching on the type alone reads a wedged function as a
/// clean shutdown — the loop unwinds, the process exits zero, and the stack sits half-up with
/// <c>docker compose ps</c> reporting success.
/// </para>
/// <para>
/// The token is what separates them. Nothing else can: the exception carries a token of its own, but
/// a timeout's is one the caller has never seen.
/// </para>
/// </remarks>
internal static class Stopping
{
    /// <summary>
    /// Whether this failure is the program being asked to stop.
    /// </summary>
    /// <param name="failure">What was thrown.</param>
    /// <param name="cancellationToken">The token a stop signal cancels.</param>
    internal static bool Requested(Exception failure, CancellationToken cancellationToken) =>
        failure is OperationCanceledException && cancellationToken.IsCancellationRequested;
}
