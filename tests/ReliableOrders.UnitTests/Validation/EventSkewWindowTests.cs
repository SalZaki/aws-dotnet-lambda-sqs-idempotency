using Microsoft.Extensions.Time.Testing;
using ReliableOrders.Core.Validation;

namespace ReliableOrders.UnitTests.Validation;

public sealed class EventSkewWindowTests
{
    [Fact]
    public void Bounds_are_kept_as_given()
    {
        var window = new EventSkewWindow(TimeSpan.FromHours(2), TimeSpan.FromDays(3));

        Assert.Equal(TimeSpan.FromHours(2), window.MaxFuture);
        Assert.Equal(TimeSpan.FromDays(3), window.MaxPast);
    }

    /// <summary>
    /// A negative bound would reject every event, and would do it at run time on real traffic rather
    /// than at the cold start that read the configuration.
    /// </summary>
    [Fact]
    public void Negative_future_bound_is_rejected()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => new EventSkewWindow(TimeSpan.FromSeconds(-1), TimeSpan.FromDays(5)));

        Assert.Equal("maxFuture", exception.ParamName);
    }

    [Fact]
    public void Negative_past_bound_is_rejected()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => new EventSkewWindow(TimeSpan.FromHours(24), TimeSpan.FromSeconds(-1)));

        Assert.Equal("maxPast", exception.ParamName);
    }

    /// <summary>
    /// Without an upper bound, a mistyped environment variable overflows when added to processing
    /// time and throws from Validate on every message, rather than failing at the cold start that
    /// read the configuration.
    /// </summary>
    [Fact]
    public void Future_bound_beyond_the_cap_is_rejected()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => new EventSkewWindow(EventSkewWindow.MaxConfigurableBound + TimeSpan.FromDays(1), TimeSpan.Zero));

        Assert.Equal("maxFuture", exception.ParamName);
    }

    [Fact]
    public void Past_bound_beyond_the_cap_is_rejected()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => new EventSkewWindow(TimeSpan.Zero, EventSkewWindow.MaxConfigurableBound + TimeSpan.FromDays(1)));

        Assert.Equal("maxPast", exception.ParamName);
    }

    [Fact]
    public void Bounds_exactly_at_the_cap_are_allowed()
    {
        var window = new EventSkewWindow(EventSkewWindow.MaxConfigurableBound, EventSkewWindow.MaxConfigurableBound);

        Assert.Equal(EventSkewWindow.MaxConfigurableBound, window.MaxFuture);
    }

    /// <summary>
    /// The cap has to leave room to add a bound to any plausible timestamp without overflowing.
    /// </summary>
    [Fact]
    public void A_window_at_the_cap_does_not_overflow_when_added_to_a_timestamp()
    {
        var window = new EventSkewWindow(EventSkewWindow.MaxConfigurableBound, EventSkewWindow.MaxConfigurableBound);
        var validator = new OrderEventValidator(new FakeTimeProvider(ValidEvent.Now), window);

        Assert.True(validator.Validate(ValidEvent.Create()).IsValid);
    }

    [Fact]
    public void Default_is_within_the_cap()
    {
        Assert.True(EventSkewWindow.Default.MaxFuture <= EventSkewWindow.MaxConfigurableBound);
        Assert.True(EventSkewWindow.Default.MaxPast <= EventSkewWindow.MaxConfigurableBound);
    }

    [Fact]
    public void Zero_bounds_are_allowed()
    {
        var window = new EventSkewWindow(TimeSpan.Zero, TimeSpan.Zero);

        Assert.Equal(TimeSpan.Zero, window.MaxFuture);
        Assert.Equal(TimeSpan.Zero, window.MaxPast);
    }

    /// <summary>
    /// The past bound must exceed the source queue's four-day retention, or a message that sat in the
    /// queue for its whole life is dead-lettered for being old when the queue is what made it old.
    /// </summary>
    [Fact]
    public void Default_past_bound_exceeds_source_queue_retention()
    {
        Assert.True(EventSkewWindow.Default.MaxPast > TimeSpan.FromDays(4));
    }

    [Fact]
    public void Default_matches_the_documented_recommendation()
    {
        Assert.Equal(TimeSpan.FromHours(24), EventSkewWindow.Default.MaxFuture);
        Assert.Equal(TimeSpan.FromDays(5), EventSkewWindow.Default.MaxPast);
    }
}
