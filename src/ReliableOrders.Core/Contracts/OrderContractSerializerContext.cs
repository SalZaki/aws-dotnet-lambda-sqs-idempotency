using System.Text.Json.Serialization;

namespace ReliableOrders.Core.Contracts;

/// <summary>
/// Source-generated serialization for the inbound contract types.
/// </summary>
/// <remarks>
/// Reads what a publisher sends, and tolerates unknown top-level fields for forward compatibility.
/// Canonical hashing uses a separate context that fixes property order and formatting. The two must
/// not be merged: a change made for the reader would alter canonicalisation, which reclassifies every
/// stored idempotency record.
/// </remarks>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(OrderCreatedV1))]
[JsonSerializable(typeof(OrderData))]
public sealed partial class OrderContractSerializerContext : JsonSerializerContext;
