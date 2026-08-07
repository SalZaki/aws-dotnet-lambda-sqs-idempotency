using System.Text.Json.Serialization;

namespace ReliableOrders.Core.Idempotency;

/// <summary>
/// Source-generated serialization for the canonical types, and the only writer whose output is
/// hashed.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately separate from <see cref="Contracts.OrderContractSerializerContext"/>. That context
/// reads what publishers send and may be tuned for the reader's convenience; this one produces hash
/// input, where a change silently reclassifies every idempotency record ever written. Merging the two
/// would put both behaviours behind one set of options.
/// </para>
/// <para>
/// Every option that decides bytes is stated even where it matches today's default, so a future
/// change to that default is inert here rather than a rewrite of the hash. What the options cannot
/// pin is the string encoder, which decides how a non-ASCII character is escaped. The committed
/// vectors cover that: one carries a non-ASCII item description precisely so a change in escaping
/// fails the build.
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    NumberHandling = JsonNumberHandling.Strict,
    IncludeFields = false)]
[JsonSerializable(typeof(CanonicalOrderCreatedV1))]
[JsonSerializable(typeof(CanonicalOrderData))]
internal sealed partial class CanonicalSerializerContext : JsonSerializerContext;
