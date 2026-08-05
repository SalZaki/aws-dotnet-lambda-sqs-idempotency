using Microsoft.Extensions.Time.Testing;
using ReliableOrders.Core.Contracts;
using ReliableOrders.Core.Validation;

namespace ReliableOrders.UnitTests.Validation;

/// <summary>
/// Padding is rejected rather than trimmed, on every text field.
/// </summary>
/// <remarks>
/// <c>orderId</c> is the domain-level idempotency key and the rest of <c>data</c> forms
/// <c>BusinessSha256</c>, so a value padded on one publish and not on its retry produces a second
/// order or a spurious conflict. Trimming would hide that and change the hash input.
/// </remarks>
public sealed class UntrimmedFieldTests
{
    private readonly OrderEventValidator _validator = new(
        new FakeTimeProvider(ValidEvent.Now),
        EventSkewWindow.Default);

    [Theory]
    [InlineData(" ORD-100001")]
    [InlineData("ORD-100001 ")]
    [InlineData("  ORD-100001  ")]
    [InlineData("\tORD-100001")]
    [InlineData("ORD-100001\n")]
    public void Padded_order_id_is_rejected(string orderId)
    {
        AssertSingleFailure(
            ValidEvent.Create() with { Data = ValidEvent.Data() with { OrderId = orderId } },
            "data.orderId",
            ValidationRule.NotTrimmed);
    }

    [Fact]
    public void Padded_customer_id_is_rejected()
    {
        AssertSingleFailure(
            ValidEvent.Create() with { Data = ValidEvent.Data() with { CustomerId = "CUS-90001 " } },
            "data.customerId",
            ValidationRule.NotTrimmed);
    }

    [Fact]
    public void Padded_item_description_is_rejected()
    {
        AssertSingleFailure(
            ValidEvent.Create() with { Data = ValidEvent.Data() with { ItemDescription = " Mechanical keyboard" } },
            "data.itemDescription",
            ValidationRule.NotTrimmed);
    }

    [Fact]
    public void Padded_source_is_rejected()
    {
        AssertSingleFailure(
            ValidEvent.Create() with { Source = "sample.order-publisher " },
            "source",
            ValidationRule.NotTrimmed);
    }

    /// <summary>
    /// Interior whitespace is content, not padding. A description is free text and must keep its
    /// spaces.
    /// </summary>
    [Fact]
    public void Interior_whitespace_is_accepted()
    {
        var result = _validator.Validate(
            ValidEvent.Create() with
            {
                Data = ValidEvent.Data() with { ItemDescription = "Mechanical keyboard, blue switches" },
            });

        Assert.True(result.IsValid);
    }

    /// <summary>
    /// A padded value is one failure, not two. Trimming is checked after length, so an over-long
    /// padded value reports the limit it broke rather than the padding.
    /// </summary>
    [Fact]
    public void Over_long_padded_value_reports_only_the_length()
    {
        var orderId = " " + new string('o', OrderContract.MaxOrderIdLength);

        AssertSingleFailure(
            ValidEvent.Create() with { Data = ValidEvent.Data() with { OrderId = orderId } },
            "data.orderId",
            ValidationRule.TooLong);
    }

    private void AssertSingleFailure(OrderCreatedV1 orderEvent, string field, string rule)
    {
        var result = _validator.Validate(orderEvent);

        Assert.False(result.IsValid);

        var failure = Assert.Single(result.Failures);
        Assert.Equal(field, failure.Field);
        Assert.Equal(rule, failure.Rule);
    }
}
