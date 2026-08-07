using ReliableOrders.Core.Contracts;
using ReliableOrders.Core.Idempotency;
using ReliableOrders.UnitTests.Validation;

namespace ReliableOrders.UnitTests.Idempotency;

/// <summary>
/// The two scopes, and what each hash must and must not react to.
/// </summary>
public sealed class CanonicalPayloadHasherTests
{
    private static readonly CanonicalPayloadHasher Hasher = new();

    /// <summary>
    /// The stored form is compared for equality against a hash written by another build. Length and
    /// case are part of that comparison, so both are asserted rather than assumed.
    /// </summary>
    [Fact]
    public void Hashes_are_sixty_four_lowercase_hexadecimal_characters()
    {
        var hashes = Hasher.ComputeHashes(ValidEvent.Create());

        Assert.All(
            [hashes.EnvelopeSha256, hashes.BusinessSha256],
            hash =>
            {
                Assert.Equal(64, hash.Length);
                Assert.All(hash, character =>
                    Assert.True(
                        char.IsAsciiDigit(character) || char.IsAsciiLetterLower(character),
                        $"'{character}' is not a lowercase hexadecimal character."));
                Assert.Equal(32, Convert.FromHexString(hash).Length);
            });
    }

    [Fact]
    public void Hashing_the_same_event_twice_produces_the_same_hashes()
    {
        var orderEvent = ValidEvent.Create();

        Assert.Equal(Hasher.ComputeHashes(orderEvent), Hasher.ComputeHashes(orderEvent));
    }

    /// <summary>
    /// Two objects with the same field values, built independently, must hash the same. Anything
    /// carried over from the instance rather than from its values would show up here.
    /// </summary>
    [Fact]
    public void Two_separately_built_events_with_the_same_values_hash_identically()
    {
        Assert.Equal(Hasher.ComputeHashes(ValidEvent.Create()), Hasher.ComputeHashes(ValidEvent.Create()));
    }

    /// <summary>
    /// An at-least-once redelivery is the same bytes twice, and must be indistinguishable from the
    /// first delivery in both scopes.
    /// </summary>
    [Fact]
    public void A_byte_identical_redelivery_matches_in_both_scopes()
    {
        var original = Hasher.ComputeHashes(Sample.ParseEvent(Sample.Valid));
        var redelivered = Hasher.ComputeHashes(Sample.ParseEvent(Sample.Duplicate));

        Assert.Equal(original, redelivered);
    }

    /// <summary>
    /// The reason two hashes exist. The republished fixture is the same order under a new event ID, a
    /// later timestamp and a different correlation ID, so its envelope hash cannot match — and if the
    /// business hash did not match, a valid order would be dead-lettered as a conflict.
    /// </summary>
    [Fact]
    public void A_republished_order_shares_the_business_hash_and_differs_in_the_envelope_hash()
    {
        var original = Hasher.ComputeHashes(Sample.ParseEvent(Sample.Valid));
        var republished = Hasher.ComputeHashes(Sample.ParseEvent(Sample.Republished));

        Assert.Equal(original.BusinessSha256, republished.BusinessSha256);
        Assert.NotEqual(original.EnvelopeSha256, republished.EnvelopeSha256);
    }

    /// <summary>
    /// The conflicting fixture differs from the republished one in <c>amountMinor</c> and nothing
    /// else. That single field is what separates a benign republish from genuine divergence.
    /// </summary>
    [Fact]
    public void Changed_business_data_changes_the_business_hash()
    {
        var republished = Hasher.ComputeHashes(Sample.ParseEvent(Sample.Republished));
        var conflicting = Hasher.ComputeHashes(Sample.ParseEvent(Sample.Conflicting));

        Assert.NotEqual(republished.BusinessSha256, conflicting.BusinessSha256);
    }

    /// <summary>
    /// Every business field reaches both hashes. A field left out of canonicalisation would let two
    /// orders that differ on it be classified as duplicates, and the stored order would keep whichever
    /// arrived first.
    /// </summary>
    [Theory]
    [InlineData("orderId")]
    [InlineData("customerId")]
    [InlineData("currency")]
    [InlineData("amountMinor")]
    [InlineData("itemDescription")]
    public void Every_business_field_changes_the_business_hash(string field)
    {
        var original = Hasher.ComputeHashes(ValidEvent.Create());
        var edited = Hasher.ComputeHashes(ValidEvent.Create() with { Data = EditBusinessField(field) });

        Assert.True(
            original.BusinessSha256 != edited.BusinessSha256,
            $"Changing data.{field} left the business hash unchanged, so it is not in the hash input.");

        Assert.True(
            original.EnvelopeSha256 != edited.EnvelopeSha256,
            $"Changing data.{field} left the envelope hash unchanged, so data is not nested in it.");
    }

    /// <summary>
    /// Every envelope field reaches the envelope hash and none of them reaches the business hash. The
    /// second half is what makes a republish survive: each field edited here differs on a legitimate
    /// republish of the same order.
    /// </summary>
    /// <remarks>
    /// <c>schemaVersion</c> and <c>eventType</c> are absent. Both are pinned to a single accepted
    /// value, so no event carrying a different one reaches hashing.
    /// </remarks>
    [Theory]
    [InlineData("eventId")]
    [InlineData("occurredAtUtc")]
    [InlineData("source")]
    [InlineData("correlationId")]
    [InlineData("causationId")]
    public void Every_envelope_field_changes_the_envelope_hash_alone(string field)
    {
        var original = Hasher.ComputeHashes(ValidEvent.Create());
        var edited = Hasher.ComputeHashes(EditEnvelopeField(field));

        Assert.True(
            original.EnvelopeSha256 != edited.EnvelopeSha256,
            $"Changing {field} left the envelope hash unchanged, so it is not in the hash input.");

        Assert.True(
            original.BusinessSha256 == edited.BusinessSha256,
            $"Changing {field} moved the business hash, which would report a republish as a conflict.");
    }

    /// <summary>
    /// An absent <c>causationId</c> and a present one are different events. The field is carried
    /// inside the envelope hash for exactly this reason, so the null case is covered separately from
    /// the value-changed case above.
    /// </summary>
    [Fact]
    public void A_present_causation_id_is_not_the_same_event_as_an_absent_one()
    {
        var rootEvent = ValidEvent.Create() with { CausationId = null };
        var causedEvent = rootEvent with { CausationId = Guid.Parse("6b1f0a53-2c4d-4a17-9f8e-3d2c5b7a91e4") };

        Assert.NotEqual(
            Hasher.ComputeHashes(rootEvent).EnvelopeSha256,
            Hasher.ComputeHashes(causedEvent).EnvelopeSha256);
    }

    [Fact]
    public void Hashing_a_null_event_throws()
    {
        Assert.Throws<ArgumentNullException>(() => Hasher.ComputeHashes(null!));
    }

    private static OrderData EditBusinessField(string field)
    {
        var data = ValidEvent.Data();

        return field switch
        {
            "orderId" => data with { OrderId = "ORD-100002" },
            "customerId" => data with { CustomerId = "CUS-90002" },
            "currency" => data with { Currency = "USD" },
            "amountMinor" => data with { AmountMinor = data.AmountMinor + 1 },
            "itemDescription" => data with { ItemDescription = "Mechanical keyboards" },
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, "No edit defined."),
        };
    }

    private static OrderCreatedV1 EditEnvelopeField(string field)
    {
        var orderEvent = ValidEvent.Create();

        return field switch
        {
            "eventId" => orderEvent with { EventId = Guid.Parse("6b1f0a53-2c4d-4a17-9f8e-3d2c5b7a91e4") },
            "occurredAtUtc" => orderEvent with { OccurredAtUtc = orderEvent.OccurredAtUtc.AddSeconds(1) },
            "source" => orderEvent with { Source = "sample.other-publisher" },
            "correlationId" => orderEvent with { CorrelationId = Guid.Parse("9c4d21b8-70a6-4f5e-8b3c-1e6f0d8a2b57") },
            "causationId" => orderEvent with { CausationId = Guid.Parse("0d76e91c-44e6-4fba-901f-bfdb76645299") },
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, "No edit defined."),
        };
    }
}
