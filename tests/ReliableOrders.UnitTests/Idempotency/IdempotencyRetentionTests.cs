using ReliableOrders.Core.Idempotency;

namespace ReliableOrders.UnitTests.Idempotency;

/// <summary>
/// The bounds a mistyped environment variable has to clear.
/// </summary>
public sealed class IdempotencyRetentionTests
{
    [Fact]
    public void The_default_is_the_thirty_days_the_correctness_model_recommends()
    {
        Assert.Equal(TimeSpan.FromDays(30), IdempotencyRetention.Default.Duration);
    }

    [Fact]
    public void A_configured_duration_is_kept_as_given()
    {
        Assert.Equal(TimeSpan.FromDays(7), new IdempotencyRetention(TimeSpan.FromDays(7)).Duration);
    }

    /// <summary>
    /// A record written already expired protects nothing, and the deployment would look healthy while
    /// duplicate detection silently did not run.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_duration_that_is_not_positive_is_rejected(int days)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new IdempotencyRetention(TimeSpan.FromDays(days)));
    }

    /// <summary>
    /// The cap catches a mistyped variable at the cold start that reads it, rather than as an expiry
    /// stamped thousands of years out that no TTL sweep would ever reach.
    /// </summary>
    [Fact]
    public void A_duration_beyond_the_configurable_maximum_is_rejected()
    {
        var beyondMaximum = IdempotencyRetention.MaxConfigurableDuration + TimeSpan.FromSeconds(1);

        Assert.Throws<ArgumentOutOfRangeException>(() => new IdempotencyRetention(beyondMaximum));
    }

    [Fact]
    public void The_configurable_maximum_itself_is_accepted()
    {
        Assert.Equal(
            IdempotencyRetention.MaxConfigurableDuration,
            new IdempotencyRetention(IdempotencyRetention.MaxConfigurableDuration).Duration);
    }
}
