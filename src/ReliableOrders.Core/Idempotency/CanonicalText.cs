using System.Globalization;

namespace ReliableOrders.Core.Idempotency;

/// <summary>
/// The text forms canonicalisation commits to, in one place because more than one caller has to agree
/// on them.
/// </summary>
/// <remarks>
/// <see cref="Identifier"/> is used by the canonical envelope and by <see cref="IdempotencyClaim.Key"/>.
/// The key the transaction writes and the identifier inside <c>EnvelopeSha256</c> have to be the same
/// string rather than two spellings of one value, and two call sites each formatting for themselves is
/// how that stops being true.
/// </remarks>
internal static class CanonicalText
{
    /// <summary>
    /// Renders an identifier as the 36-character hyphenated form, lowercase.
    /// </summary>
    /// <remarks>
    internal static string Identifier(Guid value) =>
        value.ToString("D", CultureInfo.InvariantCulture).ToLowerInvariant();

    /// <summary>
    /// Renders an instant in round-trip form, offset included.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The offset is written rather than folded into the instant. Validation has already rejected a
    /// non-zero offset, so every value reaching here ends in <c>+00:00</c>; converting to UTC first
    /// would additionally make a rejected value hash the same as its UTC spelling, quietly performing
    /// the normalisation the contract forbids. Round-trip form also fixes the fractional-second
    /// precision at seven digits, so an instant carrying milliseconds and the same instant carrying
    /// none cannot render differently.
    /// </para>
    /// <para>
    /// The serializer's default encoder escapes the offset's plus sign, so the value reads
    /// <c>\u002B00:00</c> in the hashed bytes. That is stable and it is ASCII. Relaxing the encoder to
    /// tidy it would rewrite every hash this build has ever produced, for no operational gain.
    /// </para>
    /// </remarks>
    internal static string Instant(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);
}
