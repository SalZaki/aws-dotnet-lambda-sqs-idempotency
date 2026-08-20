using System.Net.Http.Json;
using System.Text.Json;

namespace ReliableOrders.Local;

/// <summary>
/// Invokes the function through the Lambda runtime interface emulator in the container beside this
/// one.
/// </summary>
/// <remarks>
/// <para>
/// The emulator is what AWS ships inside its own base images, and it speaks the runtime API the
/// managed runtime speaks. So the handler that runs here is the deployed one, loaded by the same
/// runtime, deserialising through the same source-generated serializer, and reading a real
/// <c>ILambdaContext</c> for the remaining time the deadline is computed from. It is the invocation
/// path that is real; the mapping in front of it is the stand-in.
/// </para>
/// <para>
/// A failed invocation is reported and the batch is left alone, which is what the real mapping does:
/// nothing is deleted, the visibility timeout expires, and the whole batch is redelivered. The
/// alternative — treating a transport failure as an empty failure list — would delete a batch the
/// function never saw.
/// </para>
/// </remarks>
internal sealed class FunctionInvoker
{
    /// <summary>
    /// The header the emulator sets when the handler threw rather than returned.
    /// </summary>
    /// <remarks>
    /// It answers 200 either way, and the body of a failed invocation is an error document rather
    /// than a response. Reading the status alone would deserialise that document into a response with
    /// no failures and delete every record in the batch.
    /// </remarks>
    private const string FunctionErrorHeader = "X-Amz-Function-Error";

    private readonly HttpClient _client;
    private readonly Uri _url;

    /// <summary>
    /// Points the invoker at the emulator.
    /// </summary>
    /// <param name="client">Carries the timeout, which is the function's own plus a margin.</param>
    /// <param name="url">Where the emulator accepts an invocation.</param>
    internal FunctionInvoker(HttpClient client, Uri url)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(url);

        _client = client;
        _url = url;
    }

    /// <summary>
    /// Sends one batch and returns the identifiers the function asked to have redelivered.
    /// </summary>
    /// <param name="batch">The records, in the shape the runtime delivers them.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The failed identifiers, or null when the invocation itself failed.</returns>
    internal async Task<IReadOnlyCollection<string>?> InvokeAsync(
        SqsEventPayload batch,
        CancellationToken cancellationToken)
    {
        using var response = await _client.PostAsJsonAsync(
            _url,
            batch,
            LocalSerializerContext.Default.SqsEventPayload,
            cancellationToken);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            Log.Line($"The function answered {(int)response.StatusCode}. The batch stays on the queue. {body}");

            return null;
        }

        if (response.Headers.Contains(FunctionErrorHeader))
        {
            Log.Line($"The function threw. The batch stays on the queue. {body}");

            return null;
        }

        return Read(body);
    }

    /// <remarks>
    /// A body that will not parse is an invocation failure rather than an empty failure list, for the
    /// reason the header check above exists: the safe reading of anything unexpected is that nothing
    /// may be deleted.
    /// </remarks>
    private static IReadOnlyCollection<string>? Read(string body)
    {
        SqsBatchResponsePayload? response;

        try
        {
            response = JsonSerializer.Deserialize(body, LocalSerializerContext.Default.SqsBatchResponsePayload);
        }
        catch (JsonException malformed)
        {
            Log.Line($"The function's response is not JSON, so the batch stays on the queue: {malformed.Message}");

            return null;
        }

        if (response is null)
        {
            Log.Line("The function returned no response at all, so the batch stays on the queue.");

            return null;
        }

        // An absent list is a batch that wholly succeeded, which is how Lambda reads it. An entry with
        // no identifier is dropped rather than treated as a failure of unknown identity: SQS matches
        // on the identifier, so an entry it cannot match makes the whole batch redeliver.
        return response.BatchItemFailures is null
            ? []
            : [.. response.BatchItemFailures
                .Select(failure => failure.ItemIdentifier)
                .Where(identifier => !string.IsNullOrWhiteSpace(identifier))
                .Select(identifier => identifier!)];
    }
}
