using ReliableOrders.Core.Contracts;
using ReliableOrders.Core.Idempotency;
using ReliableOrders.UnitTests.Validation;

namespace ReliableOrders.UnitTests.Idempotency;

/// <summary>
/// Every value the transaction writes, and the rule that none of them may come from a clock.
/// </summary>
public sealed class IdempotencyClaimTests
{
    private static readonly CanonicalPayloadHasher Hasher = new();

    /// <summary>
    /// The valid event's hashes, for the guard tests where the other argument is the null being
    /// checked and the hashes only have to be a well-formed value.
    /// </summary>
    private static readonly PayloadHashes ValidEventHashes = Hasher.ComputeHashes(ValidEvent.Create());

    /// <summary>
    /// The key is the event identifier, undecorated. A prefix or an environment namespace would read
    /// as harmless and would overflow the token limit asserted below.
    /// </summary>
    [Fact]
    public void The_key_is_the_event_id_verbatim()
    {
        var orderEvent = ValidEvent.Create();

        Assert.Equal(orderEvent.EventId.ToString(), Claim(orderEvent).Key);
    }

    /// <summary>
    /// DynamoDB caps <c>ClientRequestToken</c> at 36 characters, which a hyphenated UUID exactly
    /// fills. There is no headroom, so this asserts the exact length rather than an upper bound: a
    /// key one character longer would fail the transaction outright.
    /// </summary>
    [Fact]
    public void The_client_request_token_fills_the_limit_exactly()
    {
        Assert.Equal(
            IdempotencyClaim.MaxClientRequestTokenLength,
            Claim(ValidEvent.Create()).ClientRequestToken.Length);
    }

    [Fact]
    public void The_client_request_token_is_the_key()
    {
        var claim = Claim(ValidEvent.Create());

        Assert.Equal(claim.Key, claim.ClientRequestToken);
    }

    [Fact]
    public void The_creation_stamp_is_the_event_time()
    {
        var orderEvent = ValidEvent.Create();

        Assert.Equal(orderEvent.OccurredAtUtc, Claim(orderEvent).CreatedAtUtc);
    }

    [Fact]
    public void The_expiry_is_the_event_time_plus_the_configured_retention()
    {
        var orderEvent = ValidEvent.Create();
        var retention = new IdempotencyRetention(TimeSpan.FromDays(30));

        Assert.Equal(
            orderEvent.OccurredAtUtc.AddDays(30).ToUnixTimeSeconds(),
            Claim(orderEvent, retention).ExpirationEpochSeconds);
    }

    /// <summary>
    /// The determinism rule. Two attempts at the same event build the same values, so the transaction
    /// bodies are byte-identical and a retry inside the <c>ClientRequestToken</c> window is accepted
    /// rather than failing with <c>IdempotentParameterMismatchException</c>.
    /// </summary>
    /// <remarks>
    /// There is no clock to advance between the two attempts, and that is the real guarantee — the
    /// constructor takes the event, its hashes and the retention, and nothing else. A later change
    /// that reached for a <see cref="TimeProvider"/> would have to widen the signature, which is a
    /// visible edit rather than a value that quietly starts moving. What this test can still catch is
    /// a derived value that is non-deterministic for some other reason, such as a generated
    /// identifier or an attempt counter.
    /// </remarks>
    [Fact]
    public void Two_attempts_at_the_same_event_build_the_same_claim()
    {
        var orderEvent = ValidEvent.Create();

        Assert.Equal(Claim(orderEvent), Claim(orderEvent));
    }

    /// <summary>
    /// A longer retention moves the expiry and nothing else, so the same event under two
    /// configurations still claims the same key.
    /// </summary>
    [Fact]
    public void Retention_moves_the_expiry_alone()
    {
        var orderEvent = ValidEvent.Create();

        var shortLived = Claim(orderEvent, new IdempotencyRetention(TimeSpan.FromDays(7)));
        var longLived = Claim(orderEvent, new IdempotencyRetention(TimeSpan.FromDays(30)));

        Assert.True(longLived.ExpirationEpochSeconds > shortLived.ExpirationEpochSeconds);
        Assert.Equal(shortLived.Key, longLived.Key);
        Assert.Equal(shortLived.CreatedAtUtc, longLived.CreatedAtUtc);
    }

    /// <summary>
    /// A republish is a different claim in every respect except the order it describes. Its key and
    /// expiry come from the new event, which is why the event-level record cannot recognise it and the
    /// order-level check has to.
    /// </summary>
    [Fact]
    public void A_republished_event_produces_a_different_claim()
    {
        var originalClaim = Claim(Sample.ParseEvent(Sample.Valid));
        var republishedClaim = Claim(Sample.ParseEvent(Sample.Republished));

        Assert.NotEqual(originalClaim.Key, republishedClaim.Key);
        Assert.NotEqual(originalClaim.ExpirationEpochSeconds, republishedClaim.ExpirationEpochSeconds);
        Assert.Equal(originalClaim.Hashes.BusinessSha256, republishedClaim.Hashes.BusinessSha256);
    }

    [Fact]
    public void A_null_event_throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => new IdempotencyClaim(null!, ValidEventHashes, IdempotencyRetention.Default));
    }

    [Fact]
    public void Null_hashes_throw()
    {
        Assert.Throws<ArgumentNullException>(
            () => new IdempotencyClaim(ValidEvent.Create(), null!, IdempotencyRetention.Default));
    }

    [Fact]
    public void A_null_retention_throws()
    {
        Assert.Throws<ArgumentNullException>(() => new IdempotencyClaim(ValidEvent.Create(), ValidEventHashes, null!));
    }

    /// <remarks>
    /// The hashes are computed from the event passed in rather than taken from a shared fixture, so a
    /// claim never carries hashes belonging to some other event. Nothing asserts on them today, but a
    /// fixture that pairs the wrong two values is how a later test comes to assert something untrue.
    /// </remarks>
    private static IdempotencyClaim Claim(OrderCreatedV1 orderEvent, IdempotencyRetention? retention = null) =>
        new(orderEvent, Hasher.ComputeHashes(orderEvent), retention ?? IdempotencyRetention.Default);
}
