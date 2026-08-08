using ReliableOrders.Core.Contracts;
using ReliableOrders.Core.Idempotency;
using ReliableOrders.Core.Persistence;
using ReliableOrders.UnitTests.Validation;

namespace ReliableOrders.UnitTests.Persistence;

/// <summary>
/// The two stored rows, and the rule that no value in either may come from a clock.
/// </summary>
public sealed class OrderWriteRequestTests
{
    private static readonly CanonicalPayloadHasher Hasher = new();

    [Fact]
    public void The_idempotency_key_is_the_event_id_verbatim()
    {
        var orderEvent = ValidEvent.Create();

        Assert.Equal(orderEvent.EventId.ToString(), Request(orderEvent).IdempotencyRecord.IdempotencyKey);
    }

    /// <summary>
    /// DynamoDB caps <c>ClientRequestToken</c> at 36 characters, which a hyphenated UUID exactly fills.
    /// There is no headroom, so the exact length is asserted rather than an upper bound — a key one
    /// character longer would fail the transaction outright.
    /// </summary>
    [Fact]
    public void The_client_request_token_fills_the_limit_exactly()
    {
        Assert.Equal(
            OrderWriteRequest.MaxClientRequestTokenLength,
            Request(ValidEvent.Create()).ClientRequestToken.Length);
    }

    [Fact]
    public void The_client_request_token_is_the_idempotency_key()
    {
        var request = Request(ValidEvent.Create());

        Assert.Equal(request.IdempotencyRecord.IdempotencyKey, request.ClientRequestToken);
    }

    /// <summary>
    /// The two rows must be joinable by string equality. If either side formatted the identifier for
    /// itself, an operator triaging a conflict would be unable to match a dead-lettered event to the
    /// order it collided with.
    /// </summary>
    [Fact]
    public void The_order_carries_the_same_event_identifier_as_the_idempotency_key()
    {
        var request = Request(ValidEvent.Create());

        Assert.Equal(request.IdempotencyRecord.IdempotencyKey, request.Order.EventId);
    }

    [Fact]
    public void Both_rows_carry_the_same_order_id()
    {
        var request = Request(ValidEvent.Create());

        Assert.Equal(request.Order.OrderId, request.IdempotencyRecord.OrderId);
    }

    /// <summary>
    /// Each row carries the hash its own conditional check is classified on, and neither carries the
    /// other's. An order stored without its business hash could not be compared against a later
    /// publish at all.
    /// </summary>
    [Fact]
    public void Each_row_carries_the_hash_its_own_scope_is_classified_on()
    {
        var orderEvent = ValidEvent.Create();
        var hashes = Hasher.ComputeHashes(orderEvent);
        var request = Request(orderEvent);

        Assert.Equal(hashes.EnvelopeSha256, request.IdempotencyRecord.EnvelopeSha256);
        Assert.Equal(hashes.BusinessSha256, request.Order.BusinessSha256);
        Assert.NotEqual(request.IdempotencyRecord.EnvelopeSha256, request.Order.BusinessSha256);
    }

    /// <summary>
    /// Every timestamp in both rows is the event's own. Processing time would differ between an attempt
    /// and its retry, and the deterministic request body forbids that.
    /// </summary>
    [Fact]
    public void Every_timestamp_is_the_event_time()
    {
        var orderEvent = ValidEvent.Create();
        var request = Request(orderEvent);

        Assert.Equal(orderEvent.OccurredAtUtc, request.IdempotencyRecord.OccurredAtUtc);
        Assert.Equal(orderEvent.OccurredAtUtc, request.IdempotencyRecord.CompletedAtUtc);
        Assert.Equal(orderEvent.OccurredAtUtc, request.Order.OccurredAtUtc);
        Assert.Equal(orderEvent.OccurredAtUtc, request.Order.CreatedAtUtc);
    }

    [Fact]
    public void The_expiry_is_the_event_time_plus_the_configured_retention()
    {
        var orderEvent = ValidEvent.Create();

        Assert.Equal(
            orderEvent.OccurredAtUtc.AddDays(30).ToUnixTimeSeconds(),
            Request(orderEvent, new IdempotencyRetention(TimeSpan.FromDays(30)))
                .IdempotencyRecord.ExpirationEpochSeconds);
    }

    /// <summary>
    /// The determinism rule. Two attempts at one event build identical rows, so the transaction bodies
    /// are byte-identical and a retry inside the token window is accepted rather than failing with
    /// <c>IdempotentParameterMismatchException</c>.
    /// </summary>
    /// <remarks>
    /// There is no clock to advance between the attempts, and that is the guarantee — the constructor
    /// takes the event, its hashes and the retention, and nothing else. What this still catches is a
    /// derived value that is non-deterministic for another reason, such as a generated identifier.
    /// </remarks>
    [Fact]
    public void Two_attempts_at_the_same_event_build_identical_rows()
    {
        var orderEvent = ValidEvent.Create();

        Assert.Equal(Request(orderEvent).IdempotencyRecord, Request(orderEvent).IdempotencyRecord);
        Assert.Equal(Request(orderEvent).Order, Request(orderEvent).Order);
    }

    /// <summary>
    /// Retention moves the expiry and nothing else, so the same event under two configurations still
    /// claims the same key and stores the same order.
    /// </summary>
    [Fact]
    public void Retention_moves_the_expiry_alone()
    {
        var orderEvent = ValidEvent.Create();

        var shortLived = Request(orderEvent, new IdempotencyRetention(TimeSpan.FromDays(7)));
        var longLived = Request(orderEvent, new IdempotencyRetention(TimeSpan.FromDays(30)));

        Assert.True(
            longLived.IdempotencyRecord.ExpirationEpochSeconds
            > shortLived.IdempotencyRecord.ExpirationEpochSeconds);

        Assert.Equal(shortLived.ClientRequestToken, longLived.ClientRequestToken);
        Assert.Equal(shortLived.Order, longLived.Order);
    }

    /// <summary>
    /// A republish shares the order's business data and nothing else. Its key and expiry come from the
    /// new event, which is why the event-level row cannot recognise it and the order-level check has to.
    /// </summary>
    [Fact]
    public void A_republished_event_produces_a_different_claim_over_the_same_order()
    {
        var original = Request(Sample.ParseEvent(Sample.Valid));
        var republished = Request(Sample.ParseEvent(Sample.Republished));

        Assert.NotEqual(original.ClientRequestToken, republished.ClientRequestToken);

        Assert.NotEqual(
            original.IdempotencyRecord.ExpirationEpochSeconds,
            republished.IdempotencyRecord.ExpirationEpochSeconds);

        Assert.Equal(original.Order.OrderId, republished.Order.OrderId);
        Assert.Equal(original.Order.BusinessSha256, republished.Order.BusinessSha256);
    }

    /// <summary>
    /// The business fields reach the stored order unaltered. Trimming or casing anything here would
    /// store something the business hash was not computed over.
    /// </summary>
    [Fact]
    public void The_order_stores_the_business_payload_unaltered()
    {
        var orderEvent = ValidEvent.Create();
        var order = Request(orderEvent).Order;

        Assert.Equal(orderEvent.Data.OrderId, order.OrderId);
        Assert.Equal(orderEvent.Data.CustomerId, order.CustomerId);
        Assert.Equal(orderEvent.Data.Currency, order.Currency);
        Assert.Equal(orderEvent.Data.AmountMinor, order.AmountMinor);
        Assert.Equal(orderEvent.Data.ItemDescription, order.ItemDescription);
        Assert.Equal(orderEvent.SchemaVersion, order.SchemaVersion);
        Assert.Equal(orderEvent.CorrelationId.ToString(), order.CorrelationId);
    }

    [Fact]
    public void A_null_event_throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => new OrderWriteRequest(null!, Hasher.ComputeHashes(ValidEvent.Create()), IdempotencyRetention.Default));
    }

    [Fact]
    public void Null_hashes_throw()
    {
        Assert.Throws<ArgumentNullException>(
            () => new OrderWriteRequest(ValidEvent.Create(), null!, IdempotencyRetention.Default));
    }

    [Fact]
    public void A_null_retention_throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => new OrderWriteRequest(ValidEvent.Create(), Hasher.ComputeHashes(ValidEvent.Create()), null!));
    }

    private static OrderWriteRequest Request(OrderCreatedV1 orderEvent, IdempotencyRetention? retention = null) =>
        new(orderEvent, Hasher.ComputeHashes(orderEvent), retention ?? IdempotencyRetention.Default);
}
