using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using ReliableOrders.Core.Contracts;

namespace ReliableOrders.Core.Idempotency;

/// <inheritdoc cref="IPayloadHasher"/>
/// <remarks>
/// <para>
/// Stateless and thread-safe, so one instance serves every record in a batch.
/// </para>
/// <para>
/// The raw message body is never hashed. Two publishers can send the same event with different
/// whitespace, property order or number spelling, and hashing the body would make those look like
/// different events. Canonicalisation re-renders the parsed event so only its meaning reaches the
/// hash.
/// </para>
/// </remarks>
public sealed class CanonicalPayloadHasher : IPayloadHasher
{
    /// <inheritdoc/>
    /// <remarks>
    /// The event is mapped once. Both hashes come from that single canonical instance, and the
    /// business hash covers the very <see cref="CanonicalOrderCreatedV1.Data"/> object nested in the
    /// envelope, so the two scopes cannot be canonicalised differently.
    /// </remarks>
    public PayloadHashes ComputeHashes(OrderCreatedV1 message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var canonical = CanonicalOrderCreatedV1.From(message);

        return new PayloadHashes(
            EnvelopeSha256: Hash(canonical, CanonicalSerializerContext.Default.CanonicalOrderCreatedV1),
            BusinessSha256: Hash(canonical.Data, CanonicalSerializerContext.Default.CanonicalOrderData));
    }

    /// <summary>
    /// Serializes a canonical value and returns the SHA-256 of its UTF-8 bytes, lowercase hexadecimal.
    /// </summary>
    /// <remarks>
    /// UTF-8 bytes rather than a string: hashing a <see cref="string"/> would need an encoding step
    /// that is a second place for the byte sequence to be decided. Lowercase hexadecimal because the
    /// stored value is compared for equality against a hash computed by another build, and one build
    /// writing uppercase would report every replay as a conflict.
    /// </remarks>
    private static string Hash<TCanonical>(TCanonical value, JsonTypeInfo<TCanonical> typeInfo) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, typeInfo)));
}
