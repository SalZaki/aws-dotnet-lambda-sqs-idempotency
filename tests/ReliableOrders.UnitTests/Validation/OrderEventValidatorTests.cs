using Microsoft.Extensions.Time.Testing;
using ReliableOrders.Core.Contracts;
using ReliableOrders.Core.Validation;

namespace ReliableOrders.UnitTests.Validation;

/// <summary>
/// Every contract rule gets a negative case naming the field and rule, and a positive case proving
/// the rule accepts what it should. Cases 4 to 6 of docs/testing-strategy.md live here.
/// </summary>
public sealed class OrderEventValidatorTests
{
    private readonly OrderEventValidator _validator = new(
        new FakeTimeProvider(ValidEvent.Now),
        EventSkewWindow.Default);

    [Fact]
    public void Valid_event_has_no_failures()
    {
        var result = _validator.Validate(ValidEvent.Create());

        Assert.True(result.IsValid);
        Assert.Empty(result.Failures);
    }

    [Fact]
    public void Absent_causation_id_is_not_a_failure()
    {
        var result = _validator.Validate(ValidEvent.Create() with { CausationId = null });

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Present_causation_id_is_not_a_failure()
    {
        var result = _validator.Validate(ValidEvent.Create() with { CausationId = Guid.NewGuid() });

        Assert.True(result.IsValid);
    }

    // ── eventId ──────────────────────────────────────────────────────────────

    [Fact]
    public void Empty_event_id_is_required()
    {
        AssertSingleFailure(ValidEvent.Create() with { EventId = Guid.Empty }, "eventId", ValidationRule.Required);
    }

    // ── eventType ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_event_type_is_required(string? eventType)
    {
        AssertSingleFailure(
            ValidEvent.Create() with { EventType = eventType! },
            "eventType",
            ValidationRule.Required);
    }

    [Theory]
    [InlineData("order.updated")]
    [InlineData("Order.Created")]
    [InlineData("ORDER.CREATED")]
    public void Event_type_other_than_the_expected_one_is_unexpected(string eventType)
    {
        AssertSingleFailure(
            ValidEvent.Create() with { EventType = eventType },
            "eventType",
            ValidationRule.UnexpectedValue);
    }

    [Fact]
    public void Expected_event_type_is_accepted()
    {
        var result = _validator.Validate(ValidEvent.Create() with { EventType = OrderContract.ExpectedEventType });

        Assert.True(result.IsValid);
    }

    // ── occurredAtUtc offset ─────────────────────────────────────────────────

    /// <summary>
    /// The same instant, written with an offset. Rejected rather than normalised: normalising would
    /// change what canonicalisation sees and reclassify a replay as a new event.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(-5)]
    public void Non_utc_offset_is_rejected(int offsetHours)
    {
        var offset = TimeSpan.FromHours(offsetHours);
        var withOffset = ValidEvent.Now.AddMinutes(-5).ToOffset(offset);

        AssertSingleFailure(
            ValidEvent.Create() with { OccurredAtUtc = withOffset },
            "occurredAtUtc",
            ValidationRule.NotUtc);
    }

    [Fact]
    public void Zero_offset_is_accepted()
    {
        var result = _validator.Validate(
            ValidEvent.Create() with { OccurredAtUtc = ValidEvent.Now.ToOffset(TimeSpan.Zero) });

        Assert.True(result.IsValid);
    }

    /// <summary>
    /// A non-UTC offset suppresses the skew check, so one cause produces one failure rather than two.
    /// </summary>
    [Fact]
    public void Non_utc_offset_does_not_also_report_skew()
    {
        var farFuture = ValidEvent.Now.AddYears(5).ToOffset(TimeSpan.FromHours(2));

        var result = _validator.Validate(ValidEvent.Create() with { OccurredAtUtc = farFuture });

        var failure = Assert.Single(result.Failures);
        Assert.Equal(ValidationRule.NotUtc, failure.Rule);
    }

    // ── occurredAtUtc skew window ────────────────────────────────────────────

    [Fact]
    public void Timestamp_beyond_the_future_bound_is_outside_the_window()
    {
        var beyond = ValidEvent.Now + EventSkewWindow.Default.MaxFuture + TimeSpan.FromMinutes(1);

        AssertSingleFailure(
            ValidEvent.Create() with { OccurredAtUtc = beyond },
            "occurredAtUtc",
            ValidationRule.OutsideSkewWindow);
    }

    [Fact]
    public void Timestamp_beyond_the_past_bound_is_outside_the_window()
    {
        var beyond = ValidEvent.Now - EventSkewWindow.Default.MaxPast - TimeSpan.FromMinutes(1);

        AssertSingleFailure(
            ValidEvent.Create() with { OccurredAtUtc = beyond },
            "occurredAtUtc",
            ValidationRule.OutsideSkewWindow);
    }

    /// <summary>
    /// The bounds are inclusive. A message that sat in the queue for exactly its retention still
    /// validates on the last delivery attempt.
    /// </summary>
    [Fact]
    public void Timestamp_exactly_on_either_bound_is_accepted()
    {
        var window = EventSkewWindow.Default;

        Assert.True(_validator.Validate(ValidEvent.Create() with { OccurredAtUtc = ValidEvent.Now + window.MaxFuture }).IsValid);
        Assert.True(_validator.Validate(ValidEvent.Create() with { OccurredAtUtc = ValidEvent.Now - window.MaxPast }).IsValid);
    }

    /// <summary>
    /// The window is read from configuration, not hard-coded. A narrower window rejects an event the
    /// default accepts, using the same clock.
    /// </summary>
    [Fact]
    public void Skew_bounds_come_from_configuration()
    {
        var narrow = new OrderEventValidator(
            new FakeTimeProvider(ValidEvent.Now),
            new EventSkewWindow(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1)));

        var tenMinutesOld = ValidEvent.Create() with { OccurredAtUtc = ValidEvent.Now.AddMinutes(-10) };

        Assert.True(_validator.Validate(tenMinutesOld).IsValid);
        AssertSingleFailure(narrow, tenMinutesOld, "occurredAtUtc", ValidationRule.OutsideSkewWindow);
    }

    /// <summary>
    /// The clock is read through TimeProvider. Advancing it moves an event out of the window with no
    /// change to the event.
    /// </summary>
    [Fact]
    public void Skew_is_measured_against_the_injected_clock()
    {
        var clock = new FakeTimeProvider(ValidEvent.Now);
        var validator = new OrderEventValidator(clock, EventSkewWindow.Default);
        var orderEvent = ValidEvent.Create();

        Assert.True(validator.Validate(orderEvent).IsValid);

        clock.Advance(EventSkewWindow.Default.MaxPast + TimeSpan.FromDays(1));

        var failure = Assert.Single(validator.Validate(orderEvent).Failures);
        Assert.Equal(ValidationRule.OutsideSkewWindow, failure.Rule);
    }

    // ── source ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Blank_source_is_required(string? source)
    {
        AssertSingleFailure(ValidEvent.Create() with { Source = source! }, "source", ValidationRule.Required);
    }

    [Fact]
    public void Source_beyond_its_limit_is_too_long()
    {
        var source = new string('s', OrderContract.MaxSourceLength + 1);

        AssertSingleFailure(ValidEvent.Create() with { Source = source }, "source", ValidationRule.TooLong);
    }

    [Fact]
    public void Source_exactly_at_its_limit_is_accepted()
    {
        var source = new string('s', OrderContract.MaxSourceLength);

        Assert.True(_validator.Validate(ValidEvent.Create() with { Source = source }).IsValid);
    }

    // ── correlationId ────────────────────────────────────────────────────────

    [Fact]
    public void Empty_correlation_id_is_required()
    {
        AssertSingleFailure(
            ValidEvent.Create() with { CorrelationId = Guid.Empty },
            "correlationId",
            ValidationRule.Required);
    }

    // ── data ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// An absent payload is one failure. The fields underneath cannot be judged, so reporting them as
    /// individually missing would be noise.
    /// </summary>
    [Fact]
    public void Absent_data_is_a_single_failure()
    {
        // null! is needed because OrderData is non-nullable on the contract by design. Parsing still
        // produces null when the payload omits the object, which is exactly what this rule catches.
        AssertSingleFailure(ValidEvent.Create() with { Data = null! }, "data", ValidationRule.Required);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_order_id_is_required(string? orderId)
    {
        AssertSingleFailure(WithData(d => d with { OrderId = orderId! }), "data.orderId", ValidationRule.Required);
    }

    [Fact]
    public void Order_id_beyond_its_limit_is_too_long()
    {
        var orderId = new string('o', OrderContract.MaxOrderIdLength + 1);

        AssertSingleFailure(WithData(d => d with { OrderId = orderId }), "data.orderId", ValidationRule.TooLong);
    }

    [Fact]
    public void Order_id_exactly_at_its_limit_is_accepted()
    {
        var orderId = new string('o', OrderContract.MaxOrderIdLength);

        Assert.True(_validator.Validate(WithData(d => d with { OrderId = orderId })).IsValid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_customer_id_is_required(string? customerId)
    {
        AssertSingleFailure(
            WithData(d => d with { CustomerId = customerId! }),
            "data.customerId",
            ValidationRule.Required);
    }

    [Fact]
    public void Customer_id_beyond_its_limit_is_too_long()
    {
        var customerId = new string('c', OrderContract.MaxCustomerIdLength + 1);

        AssertSingleFailure(
            WithData(d => d with { CustomerId = customerId }),
            "data.customerId",
            ValidationRule.TooLong);
    }

    [Fact]
    public void Customer_id_exactly_at_its_limit_is_accepted()
    {
        var customerId = new string('c', OrderContract.MaxCustomerIdLength);

        Assert.True(_validator.Validate(WithData(d => d with { CustomerId = customerId })).IsValid);
    }

    // ── currency ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_currency_is_required(string? currency)
    {
        AssertSingleFailure(WithData(d => d with { Currency = currency! }), "data.currency", ValidationRule.Required);
    }

    [Theory]
    [InlineData("gbp")]
    [InlineData("Gbp")]
    [InlineData("GB")]
    [InlineData("GBPP")]
    [InlineData("G8P")]
    [InlineData("G P")]
    [InlineData("£££")]
    public void Currency_that_is_not_three_uppercase_letters_is_rejected(string currency)
    {
        AssertSingleFailure(
            WithData(d => d with { Currency = currency }),
            "data.currency",
            ValidationRule.NotACurrencyCode);
    }

    [Theory]
    [InlineData("GBP")]
    [InlineData("USD")]
    [InlineData("JPY")]
    public void Three_uppercase_letters_are_accepted(string currency)
    {
        Assert.True(_validator.Validate(WithData(d => d with { Currency = currency })).IsValid);
    }

    // ── amountMinor ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(long.MinValue)]
    public void Non_positive_amount_is_rejected(long amountMinor)
    {
        AssertSingleFailure(
            WithData(d => d with { AmountMinor = amountMinor }),
            "data.amountMinor",
            ValidationRule.NotPositive);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1299)]
    [InlineData(long.MaxValue)]
    public void Positive_amount_is_accepted(long amountMinor)
    {
        Assert.True(_validator.Validate(WithData(d => d with { AmountMinor = amountMinor })).IsValid);
    }

    // ── itemDescription ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_item_description_is_required(string? description)
    {
        AssertSingleFailure(
            WithData(d => d with { ItemDescription = description! }),
            "data.itemDescription",
            ValidationRule.Required);
    }

    [Fact]
    public void Item_description_beyond_its_limit_is_too_long()
    {
        var description = new string('d', OrderContract.MaxItemDescriptionLength + 1);

        AssertSingleFailure(
            WithData(d => d with { ItemDescription = description }),
            "data.itemDescription",
            ValidationRule.TooLong);
    }

    [Fact]
    public void Item_description_exactly_at_its_limit_is_accepted()
    {
        var description = new string('d', OrderContract.MaxItemDescriptionLength);

        Assert.True(_validator.Validate(WithData(d => d with { ItemDescription = description })).IsValid);
    }

    // ── reporting ────────────────────────────────────────────────────────────

    /// <summary>
    /// Every broken rule is reported, not just the first. A publisher fixing one field per redelivery
    /// would otherwise need as many round trips as it has mistakes.
    /// </summary>
    [Fact]
    public void All_broken_rules_are_reported_together()
    {
        // A different combination from the one the invalid fixture covers, so the two tests do not
        // assert the same list twice. This one exercises every rule that fixture does not.
        var broken = ValidEvent.Create() with
        {
            EventId = Guid.Empty,
            EventType = "order.updated",
            Source = new string('s', OrderContract.MaxSourceLength + 1),
            Data = ValidEvent.Data() with
            {
                CustomerId = " CUS-90001",
                ItemDescription = new string('d', OrderContract.MaxItemDescriptionLength + 1),
            },
        };

        var result = _validator.Validate(broken);

        Assert.False(result.IsValid);
        Assert.Equal(
            [
                new ValidationFailure("eventId", ValidationRule.Required),
                new ValidationFailure("eventType", ValidationRule.UnexpectedValue),
                new ValidationFailure("source", ValidationRule.TooLong),
                new ValidationFailure("data.customerId", ValidationRule.NotTrimmed),
                new ValidationFailure("data.itemDescription", ValidationRule.TooLong),
            ],
            result.Failures);
    }

    /// <summary>
    /// A failure names the field and the rule, never the value. Failures are logged and counted, and
    /// the value is the publisher's payload.
    /// </summary>
    [Fact]
    public void Failures_never_carry_the_offending_value()
    {
        const string secret = "4111111111111111";

        // Both values must actually break a rule, or the assertion below inspects nothing. The
        // customer ID is repeated past its limit, since one copy is well inside it.
        var overLong = string.Concat(Enumerable.Repeat(secret, 8));

        var result = _validator.Validate(WithData(d => d with { CustomerId = overLong, Currency = secret }));

        Assert.Equal(2, result.Failures.Count);
        Assert.All(result.Failures, failure =>
        {
            Assert.DoesNotContain(secret, failure.Field, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, failure.Rule, StringComparison.Ordinal);
        });
    }

    private static OrderCreatedV1 WithData(Func<OrderData, OrderData> change) =>
        ValidEvent.Create() with { Data = change(ValidEvent.Data()) };

    private void AssertSingleFailure(OrderCreatedV1 orderEvent, string field, string rule) =>
        AssertSingleFailure(_validator, orderEvent, field, rule);

    private static void AssertSingleFailure(
        OrderEventValidator validator,
        OrderCreatedV1 orderEvent,
        string field,
        string rule)
    {
        var result = validator.Validate(orderEvent);

        Assert.False(result.IsValid);

        var failure = Assert.Single(result.Failures);
        Assert.Equal(field, failure.Field);
        Assert.Equal(rule, failure.Rule);
    }
}
