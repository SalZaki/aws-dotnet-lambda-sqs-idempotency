using Microsoft.Extensions.Time.Testing;
using ReliableOrders.Core.Validation;

namespace ReliableOrders.UnitTests.Validation;

/// <summary>
/// The guards against a null argument. No event content causes a throw, however malformed, so these
/// are the only ways out of the validator that are not a <see cref="ValidationResult"/>.
/// </summary>
public sealed class OrderEventValidatorGuardTests
{
    [Fact]
    public void Null_time_provider_is_rejected()
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => new OrderEventValidator(null!, EventSkewWindow.Default));

        Assert.Equal("timeProvider", exception.ParamName);
    }

    [Fact]
    public void Null_skew_window_is_rejected()
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => new OrderEventValidator(new FakeTimeProvider(ValidEvent.Now), null!));

        Assert.Equal("skewWindow", exception.ParamName);
    }

    [Fact]
    public void Null_event_is_rejected()
    {
        var validator = new OrderEventValidator(new FakeTimeProvider(ValidEvent.Now), EventSkewWindow.Default);

        var exception = Assert.Throws<ArgumentNullException>(() => validator.Validate(null!));

        Assert.Equal("orderEvent", exception.ParamName);
    }
}
