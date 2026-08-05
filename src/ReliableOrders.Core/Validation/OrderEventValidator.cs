using ReliableOrders.Core.Contracts;

namespace ReliableOrders.Core.Validation;

/// <inheritdoc cref="IOrderEventValidator"/>
/// <remarks>
/// <c>schemaVersion</c> is absent from these rules on purpose. The parser rejects an unsupported
/// version before an event of this type exists, so re-checking here would be a branch no input can
/// reach.
/// </remarks>
public sealed class OrderEventValidator : IOrderEventValidator
{
    private readonly TimeProvider _timeProvider;
    private readonly EventSkewWindow _skewWindow;

    /// <param name="timeProvider">
    /// Supplies processing time for the skew rule. Injected so the bound is testable without waiting,
    /// and never used for a value that gets persisted.
    /// </param>
    /// <param name="skewWindow">Bounds from configuration.</param>
    public OrderEventValidator(TimeProvider timeProvider, EventSkewWindow skewWindow)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(skewWindow);

        _timeProvider = timeProvider;
        _skewWindow = skewWindow;
    }

    /// <inheritdoc/>
    public ValidationResult Validate(OrderCreatedV1 orderEvent)
    {
        ArgumentNullException.ThrowIfNull(orderEvent);

        var failures = new List<ValidationFailure>();

        ValidateEnvelope(orderEvent, failures);
        ValidateData(orderEvent.Data, failures);

        return failures.Count == 0 ? ValidationResult.Valid : new ValidationResult(failures);
    }

    private void ValidateEnvelope(OrderCreatedV1 orderEvent, List<ValidationFailure> failures)
    {
        if (orderEvent.EventId == Guid.Empty)
        {
            failures.Add(new ValidationFailure("eventId", ValidationRule.Required));
        }

        if (string.IsNullOrWhiteSpace(orderEvent.EventType))
        {
            failures.Add(new ValidationFailure("eventType", ValidationRule.Required));
        }
        else if (!string.Equals(orderEvent.EventType, OrderContract.ExpectedEventType, StringComparison.Ordinal))
        {
            failures.Add(new ValidationFailure("eventType", ValidationRule.UnexpectedValue));
        }

        ValidateOccurredAt(orderEvent.OccurredAtUtc, failures);
        ValidateRequiredText(orderEvent.Source, "source", OrderContract.MaxSourceLength, failures);

        if (orderEvent.CorrelationId == Guid.Empty)
        {
            failures.Add(new ValidationFailure("correlationId", ValidationRule.Required));
        }

        // causationId carries no rule. It is optional, so absence is not a failure, and the contract
        // places no constraint on a value that is present.
    }

    /// <remarks>
    /// A non-zero offset is rejected rather than normalised. The same instant written as
    /// <c>+01:00</c> and as <c>Z</c> canonicalises differently, so normalising here would change the
    /// hash and reclassify a replay as a new event.
    /// </remarks>
    private void ValidateOccurredAt(DateTimeOffset occurredAtUtc, List<ValidationFailure> failures)
    {
        if (occurredAtUtc.Offset != TimeSpan.Zero)
        {
            failures.Add(new ValidationFailure("occurredAtUtc", ValidationRule.NotUtc));

            // The skew rule is skipped rather than also reported. Both failures have the same cause,
            // and a publisher fixing the offset fixes the window too.
            return;
        }

        var now = _timeProvider.GetUtcNow();

        if (occurredAtUtc > now + _skewWindow.MaxFuture || occurredAtUtc < now - _skewWindow.MaxPast)
        {
            failures.Add(new ValidationFailure("occurredAtUtc", ValidationRule.OutsideSkewWindow));
        }
    }

    private static void ValidateData(OrderData? data, List<ValidationFailure> failures)
    {
        if (data is null)
        {
            // Absent from the payload. Reported once; the fields underneath cannot be judged.
            failures.Add(new ValidationFailure("data", ValidationRule.Required));
            return;
        }

        ValidateRequiredText(data.OrderId, "data.orderId", OrderContract.MaxOrderIdLength, failures);
        ValidateRequiredText(data.CustomerId, "data.customerId", OrderContract.MaxCustomerIdLength, failures);
        ValidateCurrency(data.Currency, failures);

        if (data.AmountMinor <= 0)
        {
            failures.Add(new ValidationFailure("data.amountMinor", ValidationRule.NotPositive));
        }

        ValidateRequiredText(
            data.ItemDescription,
            "data.itemDescription",
            OrderContract.MaxItemDescriptionLength,
            failures);
    }

    /// <remarks>
    /// Checked character by character rather than with a culture-aware comparison. The build runs
    /// with invariant globalization, so <c>ToUpper</c> and friends have no culture data to work from,
    /// and an ISO 4217 code is ASCII by definition.
    /// </remarks>
    private static void ValidateCurrency(string? currency, List<ValidationFailure> failures)
    {
        if (string.IsNullOrWhiteSpace(currency))
        {
            failures.Add(new ValidationFailure("data.currency", ValidationRule.Required));
            return;
        }

        if (currency.Length != OrderContract.CurrencyLength || !currency.All(char.IsAsciiLetterUpper))
        {
            failures.Add(new ValidationFailure("data.currency", ValidationRule.NotACurrencyCode));
        }
    }

    /// <remarks>
    /// At most one failure per field. A null value has no length to judge, and reporting several
    /// problems for one field would suggest several separate fixes.
    /// </remarks>
    private static void ValidateRequiredText(
        string? value,
        string field,
        int maxLength,
        List<ValidationFailure> failures)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            failures.Add(new ValidationFailure(field, ValidationRule.Required));
        }
        else if (value.Length > maxLength)
        {
            failures.Add(new ValidationFailure(field, ValidationRule.TooLong));
        }
        else if (value.AsSpan().Trim().Length != value.Length)
        {
            failures.Add(new ValidationFailure(field, ValidationRule.NotTrimmed));
        }
    }
}
