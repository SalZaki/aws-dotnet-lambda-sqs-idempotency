using System.Text;
using Amazon.Lambda.Serialization.SystemTextJson;
using Amazon.Lambda.SQSEvents;
using ReliableOrders.Function.Serialization;

namespace ReliableOrders.UnitTests.Composition;

/// <summary>
/// Case 23 of the plan: what the configured serializer actually writes.
/// </summary>
/// <remarks>
/// <para>
/// Every assertion here reads bytes, and that is the whole point. With source generation and no
/// reflection fallback, a response type the context does not know serialises to <c>{}</c>. Lambda
/// reads that as an empty failure list and marks the entire batch successful, deleting every failed
/// record with no exception, no error metric and nothing in the logs. The returned object is correct
/// throughout, so a test asserting on it passes while the service silently loses messages.
/// </para>
/// <para>
/// The serializer is constructed exactly as the assembly-level attribute on <c>Function</c> declares
/// it. A test that built its own <c>JsonSerializerOptions</c> would be asserting about a serializer
/// the runtime never uses.
/// </para>
/// </remarks>
public sealed class LambdaSerializerTests
{
    /// <summary>
    /// A failure list survives serialization with its entries intact.
    /// </summary>
    [Fact]
    public void A_failure_response_serialises_with_its_identifiers()
    {
        var json = Serialize(new SQSBatchResponse
        {
            BatchItemFailures =
            [
                new SQSBatchResponse.BatchItemFailure { ItemIdentifier = "m-1" },
                new SQSBatchResponse.BatchItemFailure { ItemIdentifier = "m-2" },
            ],
        });

        Assert.Contains("batchItemFailures", json, StringComparison.Ordinal);
        Assert.Contains("itemIdentifier", json, StringComparison.Ordinal);
        Assert.Contains("m-1", json, StringComparison.Ordinal);
        Assert.Contains("m-2", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// The failure list is not an empty object.
    /// </summary>
    /// <remarks>
    /// The exact shape of the defect this case exists for. An unregistered response type writes
    /// <c>{}</c>, and an unregistered nested type writes entries that are each <c>{}</c> — Lambda
    /// treats both as no failures at all.
    /// </remarks>
    [Fact]
    public void A_failure_response_is_not_an_empty_object()
    {
        var json = Serialize(new SQSBatchResponse
        {
            BatchItemFailures = [new SQSBatchResponse.BatchItemFailure { ItemIdentifier = "m-1" }],
        });

        Assert.NotEqual("{}", json);
        Assert.DoesNotContain("[{}]", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// A clean batch serialises to a present, empty list.
    /// </summary>
    /// <remarks>
    /// Right for the opposite reason to everything above: here an empty list is the truth. It is
    /// asserted so the two cases cannot be confused by a change that makes every response empty.
    /// </remarks>
    [Fact]
    public void A_clean_response_serialises_to_an_empty_list()
    {
        var json = Serialize(new SQSBatchResponse { BatchItemFailures = [] });

        Assert.Contains("batchItemFailures", json, StringComparison.Ordinal);
        Assert.DoesNotContain("itemIdentifier", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// An inbound batch round-trips, so the handler sees the records the runtime was given.
    /// </summary>
    [Fact]
    public void An_inbound_batch_deserialises_with_its_records()
    {
        const string payload = """
            {"Records":[{"messageId":"m-1","body":"{}","attributes":{"ApproximateReceiveCount":"3"}}]}
            """;

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(payload));

        var batch = Serializer.Deserialize<SQSEvent>(stream);

        var record = Assert.Single(batch.Records);

        Assert.Equal("m-1", record.MessageId);
        Assert.Equal("3", Assert.Contains("ApproximateReceiveCount", record.Attributes));
    }

    /// <summary>
    /// The serializer under test is the one the assembly attribute names.
    /// </summary>
    private static readonly SourceGeneratorLambdaJsonSerializer<LambdaSerializerContext> Serializer = new();

    private static string Serialize(SQSBatchResponse response)
    {
        using var stream = new MemoryStream();

        Serializer.Serialize(response, stream);

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
