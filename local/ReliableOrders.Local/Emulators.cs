using System.Diagnostics;

namespace ReliableOrders.Local;

/// <summary>
/// Waits for an emulator to answer before anything is asked of it.
/// </summary>
/// <remarks>
/// <para>
/// Compose starts every service at once, and both emulators take seconds to become useful — one is a
/// JVM, the other loads a service and activates a licence. Without a wait, the first call of the
/// provisioning step races them and fails as a refused connection, which reads as a stack that is
/// broken rather than one that is not ready yet.
/// </para>
/// <para>
/// A Compose health check would be the obvious alternative and is only half available:
/// <c>amazon/dynamodb-local</c> carries no shell tooling to probe itself with. Waiting from here
/// covers both emulators the same way and can say which one it is waiting for.
/// </para>
/// </remarks>
internal static class Emulators
{
    /// <summary>
    /// How long an emulator is given before the wait is called off.
    /// </summary>
    /// <remarks>
    /// Generous rather than tuned. LocalStack activates its licence over the network on a cold start
    /// and pulls nothing else this long; the ceiling exists so a container that is never going to
    /// answer says so, rather than leaving <c>docker compose up</c> apparently working.
    /// </remarks>
    private static readonly TimeSpan Ceiling = TimeSpan.FromMinutes(3);

    /// <summary>How long between attempts.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Calls <paramref name="probe"/> until it succeeds, or gives up naming the endpoint.
    /// </summary>
    /// <param name="name">What is being waited for, for the line that says so.</param>
    /// <param name="endpoint">Where it is, for the message if it never answers.</param>
    /// <param name="probe">A cheap call that succeeds only once the emulator can serve.</param>
    /// <param name="cancellationToken">Ends the wait, which is what a stop signal cancels.</param>
    /// <exception cref="TimeoutException">The emulator did not answer within the ceiling.</exception>
    internal static async Task WaitForAsync(
        string name,
        Uri endpoint,
        Func<CancellationToken, Task> probe,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(probe);

        var elapsed = Stopwatch.StartNew();
        var announced = false;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await probe(cancellationToken);

                Log.Line($"{name} is ready.");

                return;
            }
            catch (Exception unready) when (!Stopping.Requested(unready, cancellationToken))
            {
                if (elapsed.Elapsed > Ceiling)
                {
                    throw new TimeoutException(
                        $"{name} at {endpoint} did not answer within {Ceiling.TotalSeconds:N0} seconds. The last "
                        + $"attempt failed with: {unready.Message}",
                        unready);
                }

                // Announced once rather than on every attempt, so a slow start is one line and not a
                // wall of them, and the reason is still there when it turns into a timeout.
                if (!announced)
                {
                    announced = true;

                    Log.Line($"Waiting for {name} at {endpoint}: {unready.Message}");
                }
            }

            await Task.Delay(Interval, cancellationToken);
        }
    }
}
