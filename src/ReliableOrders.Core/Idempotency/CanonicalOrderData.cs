using System.Text.Json.Serialization;
using ReliableOrders.Core.Contracts;

namespace ReliableOrders.Core.Idempotency;

/// <summary>
/// The hash input for <c>BusinessSha256</c>: the business payload in the exact shape that is
/// serialized and hashed.
/// </summary>
/// <remarks>
/// <para>
/// Separate from <see cref="OrderData"/> although the fields match today. The contract type is shaped
/// by what publishers send and will change as the contract does; this type is shaped by what has
/// already been hashed and must not. Keeping them apart is what stops a field added to the contract
/// silently entering the hash.
/// </para>
/// <para>
/// Every property carries an explicit name and order. Both are load-bearing — they decide the bytes —
/// and leaving either to a serializer default would let a policy change elsewhere rewrite the hash of
/// every stored order.
/// </para>
/// </remarks>
internal sealed record CanonicalOrderData(
    [property: JsonPropertyName("orderId"), JsonPropertyOrder(1)] string OrderId,
    [property: JsonPropertyName("customerId"), JsonPropertyOrder(2)] string CustomerId,
    [property: JsonPropertyName("currency"), JsonPropertyOrder(3)] string Currency,
    [property: JsonPropertyName("amountMinor"), JsonPropertyOrder(4)] long AmountMinor,
    [property: JsonPropertyName("itemDescription"), JsonPropertyOrder(5)] string ItemDescription)
{
    /// <summary>
    /// Maps a validated business payload into canonical form.
    /// </summary>
    /// <remarks>
    /// No value is trimmed, cased, rounded or otherwise adjusted. Validation has already rejected the
    /// shapes that would tempt an adjustment here, and adjusting a value at this point would make two
    /// wire payloads the contract calls different hash the same.
    /// </remarks>
    internal static CanonicalOrderData From(OrderData data) => new(
        OrderId: data.OrderId,
        CustomerId: data.CustomerId,
        Currency: data.Currency,
        AmountMinor: data.AmountMinor,
        ItemDescription: data.ItemDescription);
}
