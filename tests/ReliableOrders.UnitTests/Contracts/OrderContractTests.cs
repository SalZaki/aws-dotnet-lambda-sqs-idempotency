using ReliableOrders.Core.Contracts;

namespace ReliableOrders.UnitTests.Contracts;

/// <summary>
/// Makes the item-sizing argument on <see cref="OrderContract.MaxItemDescriptionLength"/> executable.
/// Without this the 400 KB claim is a comment, and raising a limit breaks nothing until a production
/// item is rejected.
/// </summary>
public sealed class OrderContractTests
{
    /// <summary>
    /// DynamoDB's hard per-item ceiling.
    /// </summary>
    private const int DynamoDbMaxItemBytes = 400 * 1024;

    /// <summary>
    /// The headroom "well under" is taken to mean. The worst case is around 1.5 KB, so this leaves
    /// room to add attributes without the budget becoming the reason a change is rejected.
    /// </summary>
    private const int OrderItemBudgetBytes = 8 * 1024;

    /// <summary>
    /// Attribute values the order item carries beyond the contract's variable-length fields. Sized
    /// from the formats they are written in rather than guessed: a UUID in canonical form is 36
    /// characters, SHA-256 in lowercase hexadecimal is 64, an ISO 8601 UTC timestamp is at most 30,
    /// and an epoch-seconds TTL is 10.
    /// </summary>
    private const int EventIdBytes = 36;
    private const int Sha256HexBytes = 64;
    private const int TimestampBytes = 30;
    private const int TtlBytes = 10;

    /// <summary>
    /// DynamoDB counts attribute names against the item size, so they are budgeted rather than
    /// ignored. Deliberately generous: roughly twenty attributes at ten characters.
    /// </summary>
    private const int AttributeNameBytes = 200;

    /// <summary>
    /// The order item is the larger of the two the transaction writes, so it sets the ceiling. Every
    /// variable-length term reads from <see cref="OrderContract"/>, so raising a limit moves this
    /// number and, if the increase is large enough, fails the build.
    /// </summary>
    private static int WorstCaseOrderItemBytes =>
        OrderContract.MaxOrderIdLength
        + OrderContract.MaxCustomerIdLength
        + OrderContract.CurrencyLength
        + OrderContract.MaxItemDescriptionLength
        + long.MinValue.ToString(System.Globalization.CultureInfo.InvariantCulture).Length
        + EventIdBytes
        + Sha256HexBytes
        + TimestampBytes
        + TtlBytes
        + AttributeNameBytes;

    [Fact]
    public void Worst_case_order_item_fits_within_the_dynamodb_item_limit()
    {
        Assert.True(
            WorstCaseOrderItemBytes < DynamoDbMaxItemBytes,
            $"Worst-case order item is {WorstCaseOrderItemBytes} bytes against DynamoDB's "
            + $"{DynamoDbMaxItemBytes} byte limit. A field limit in OrderContract is too large.");
    }

    [Fact]
    public void Worst_case_order_item_keeps_its_headroom()
    {
        Assert.True(
            WorstCaseOrderItemBytes < OrderItemBudgetBytes,
            $"Worst-case order item is {WorstCaseOrderItemBytes} bytes against a {OrderItemBudgetBytes} "
            + "byte budget. It still fits DynamoDB's 400 KB limit, but the headroom the field limits "
            + "were chosen for has gone. Recalculate the sizing on OrderContract.MaxItemDescriptionLength "
            + "and raise the budget deliberately, or lower the field limit.");
    }

    /// <summary>
    /// The idempotency item holds no free text, so it cannot approach the order item's size. Asserted
    /// so that stops being true loudly if a future story adds a field to it.
    /// </summary>
    [Fact]
    public void Idempotency_item_is_smaller_than_the_order_item()
    {
        var worstCaseIdempotencyItem =
            EventIdBytes
            + OrderContract.MaxOrderIdLength
            + Sha256HexBytes
            + TimestampBytes
            + TtlBytes
            + AttributeNameBytes;

        Assert.True(worstCaseIdempotencyItem < WorstCaseOrderItemBytes);
    }

    [Fact]
    public void Message_body_bound_matches_the_sqs_maximum()
    {
        Assert.Equal(256 * 1024, OrderContract.MaxMessageBodyCharacters);
    }
}
