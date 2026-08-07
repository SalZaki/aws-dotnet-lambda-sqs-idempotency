using ReliableOrders.Core.Contracts;
using ReliableOrders.Core.Idempotency;

namespace ReliableOrders.UnitTests.Idempotency;

/// <summary>
/// The committed known-answer vectors, which are what make determinism testable at all.
/// </summary>
/// <remarks>
/// Hashing an event twice in one process proves nothing: both values move together when the
/// serializer, the runtime or the canonical model changes. The constants in
/// <c>Vectors/hash-vectors.json</c> were computed by an earlier build in a different process and do
/// not move, so a change to canonicalisation shows up here as a mismatch. Every idempotency record
/// already stored was written against those constants, so a failure means stored records would now be
/// reclassified — a migration, not a test to update.
/// </remarks>
public sealed class HashVectorTests
{
    private static readonly CanonicalPayloadHasher Hasher = new();

    [Theory]
    [MemberData(nameof(VectorNames))]
    public void A_vector_hashes_to_its_committed_constants(string name)
    {
        var vector = HashVectors.Named(name);
        var hashes = Compute(vector);

        Assert.True(
            vector.EnvelopeSha256 == hashes.EnvelopeSha256,
            $"Vector '{vector.Name}' expected envelope hash {vector.EnvelopeSha256} but canonicalisation "
            + $"now produces {hashes.EnvelopeSha256}. {vector.Why} Every stored idempotency record was "
            + "written against the committed value; treat this as a schema migration.");

        Assert.True(
            vector.BusinessSha256 == hashes.BusinessSha256,
            $"Vector '{vector.Name}' expected business hash {vector.BusinessSha256} but canonicalisation "
            + $"now produces {hashes.BusinessSha256}. {vector.Why} Every stored order was written against "
            + "the committed value; treat this as a schema migration.");
    }

    /// <summary>
    /// The forward-compatibility rule, committed rather than merely observed. Fields this schema
    /// version does not know about are dropped by canonicalisation, so an event carrying them is a
    /// duplicate of the same event without them.
    /// </summary>
    [Fact]
    public void The_unknown_field_vector_carries_the_reference_vector_hashes()
    {
        var reference = HashVectors.Named(HashVectors.Reference);
        var extended = HashVectors.Named(HashVectors.UnknownTopLevelFields);

        Assert.Equal(reference.EnvelopeSha256, extended.EnvelopeSha256);
        Assert.Equal(reference.BusinessSha256, extended.BusinessSha256);
    }

    /// <summary>
    /// The two scopes, committed. A republish shares the business hash and cannot share the envelope
    /// hash, which is what keeps a valid order out of the dead-letter queue.
    /// </summary>
    [Fact]
    public void The_republish_vector_shares_the_business_hash_and_not_the_envelope_hash()
    {
        var reference = HashVectors.Named(HashVectors.Reference);
        var republished = HashVectors.Named(HashVectors.SameDataNewEventId);

        Assert.Equal(reference.BusinessSha256, republished.BusinessSha256);
        Assert.NotEqual(reference.EnvelopeSha256, republished.EnvelopeSha256);
    }

    /// <summary>
    /// A causation identifier is envelope scope, committed. The caused vector differs from the
    /// reference vector in that field alone.
    /// </summary>
    [Fact]
    public void The_caused_event_vector_differs_from_the_reference_vector_in_the_envelope_alone()
    {
        var reference = HashVectors.Named(HashVectors.Reference);
        var caused = HashVectors.Named(HashVectors.CausedEvent);

        Assert.Equal(reference.BusinessSha256, caused.BusinessSha256);
        Assert.NotEqual(reference.EnvelopeSha256, caused.EnvelopeSha256);
    }

    /// <summary>
    /// A placeholder left in the file would make every assertion above pass against itself. The
    /// committed values must be real hashes.
    /// </summary>
    [Theory]
    [MemberData(nameof(VectorNames))]
    public void A_vector_commits_to_a_well_formed_hash(string name)
    {
        var vector = HashVectors.Named(name);

        Assert.All(
            [vector.EnvelopeSha256, vector.BusinessSha256],
            hash =>
            {
                Assert.Equal(64, hash.Length);
                Assert.Equal(32, Convert.FromHexString(hash).Length);
                Assert.NotEqual(new string('0', 64), hash);
            });
    }

    public static TheoryData<string> VectorNames()
    {
        var names = new TheoryData<string>();

        foreach (var vector in HashVectors.All)
        {
            names.Add(vector.Name);
        }

        return names;
    }

    private static PayloadHashes Compute(HashVector vector) =>
        Hasher.ComputeHashes(Assert.IsType<ParseResult.Parsed>(new OrderEventParser().Parse(vector.Body)).Event);
}
