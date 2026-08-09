using ReliableOrders.Aws.Sqs;

namespace ReliableOrders.UnitTests.Processing;

/// <summary>
/// Case 22 of the plan: the failure list never carries an identifier Lambda cannot match.
/// </summary>
/// <remarks>
/// The cost of one bad entry is the whole batch. Lambda reprocesses every record when the list
/// contains an identifier it does not recognise, so a null, a blank or a duplicate turns a one-record
/// failure into a ten-record replay — with the successes replayed too, and nothing anywhere saying
/// so.
/// </remarks>
public sealed class BatchItemFailuresTests
{
    [Fact]
    public void Failed_identifiers_are_returned_in_order()
    {
        var response = BatchItemFailures.From(["m-1", "m-2", "m-3"]);

        Assert.Equal(["m-1", "m-2", "m-3"], Identifiers(response));
    }

    /// <summary>
    /// Nothing failed, so the list is empty — and present.
    /// </summary>
    /// <remarks>
    /// Empty is not null. A response serialising to an absent list reads to Lambda as an empty one,
    /// which happens to be right here and catastrophically wrong when records did fail; the
    /// serializer round-trip that guards the difference belongs to the composition root.
    /// </remarks>
    [Fact]
    public void A_clean_batch_returns_an_empty_list()
    {
        var response = BatchItemFailures.From([]);

        Assert.NotNull(response.BatchItemFailures);
        Assert.Empty(response.BatchItemFailures);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public void An_identifier_that_is_not_one_is_dropped(string? messageId)
    {
        var response = BatchItemFailures.From(["m-1", messageId, "m-2"]);

        Assert.Equal(["m-1", "m-2"], Identifiers(response));
    }

    /// <summary>
    /// A repeated identifier appears once, in its first position.
    /// </summary>
    [Fact]
    public void A_duplicate_identifier_is_returned_once()
    {
        var response = BatchItemFailures.From(["m-1", "m-2", "m-1"]);

        Assert.Equal(["m-1", "m-2"], Identifiers(response));
    }

    /// <summary>
    /// Identifiers differing only in case are different identifiers.
    /// </summary>
    /// <remarks>
    /// SQS message identifiers are case-sensitive, so collapsing them would drop a record that
    /// genuinely failed. The opposite choice to the one the mapper makes for attribute names, and for
    /// the opposite reason: those name W3C headers, these name records.
    /// </remarks>
    [Fact]
    public void Identifiers_differing_only_in_case_are_both_kept()
    {
        var response = BatchItemFailures.From(["m-A", "m-a"]);

        Assert.Equal(["m-A", "m-a"], Identifiers(response));
    }

    private static string[] Identifiers(Amazon.Lambda.SQSEvents.SQSBatchResponse response) =>
        [.. response.BatchItemFailures.Select(failure => failure.ItemIdentifier)];
}
