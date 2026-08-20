using System.Globalization;

namespace ReliableOrders.Local;

/// <summary>
/// What this program writes to standard output.
/// </summary>
/// <remarks>
/// <para>
/// Plain console writes rather than <c>ILogger</c>. The function's own output is structured because
/// CloudWatch parses it and the alarms are built on it; nothing parses this. What a developer wants
/// from the stack's own narration is a readable line saying which flow just happened, next to the
/// function's JSON in the same <c>docker compose</c> output.
/// </para>
/// <para>
/// Timestamped in UTC, so a line here can be lined up against the function's own <c>timestamp</c>
/// field when a demonstration does something unexpected. Compose already prefixes each line with the
/// service that wrote it, so nothing here names itself.
/// </para>
/// </remarks>
internal static class Log
{
    /// <summary>
    /// Writes one line.
    /// </summary>
    /// <param name="message">What happened.</param>
    internal static void Line(string message) =>
        Console.WriteLine(
            $"{DateTimeOffset.UtcNow.ToString(Timestamp, CultureInfo.InvariantCulture)}  {message}");

    /// <summary>
    /// The shape of the timestamp, matching the one the function writes on every log line.
    /// </summary>
    private const string Timestamp = "yyyy-MM-ddTHH:mm:ss.fffZ";
}
