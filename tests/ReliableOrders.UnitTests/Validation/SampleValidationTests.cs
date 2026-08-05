using Microsoft.Extensions.Time.Testing;
using ReliableOrders.Core.Contracts;
using ReliableOrders.Core.Validation;

namespace ReliableOrders.UnitTests.Validation;

/// <summary>
/// Runs the shipped fixtures through the real parser and validator, so samples/README.md describes
/// what the code does rather than what it was intended to do when the fixtures were written.
/// </summary>
public sealed class SampleValidationTests
{
    /// <summary>
    /// The fixtures are stamped 1 August 2026, so processing time has to sit near them or the skew
    /// rule fails every one of them for reasons unrelated to what each fixture is testing.
    /// </summary>
    private static readonly DateTimeOffset ProcessingTime = new(2026, 8, 1, 15, 0, 0, TimeSpan.Zero);

    private readonly OrderEventParser _parser = new();
    private readonly OrderEventValidator _validator = new(
        new FakeTimeProvider(ProcessingTime),
        EventSkewWindow.Default);

    [Theory]
    [InlineData(Sample.Valid)]
    [InlineData(Sample.Duplicate)]
    [InlineData(Sample.Republished)]
    [InlineData(Sample.Conflicting)]
    public void Deliverable_samples_are_valid(string sampleFileName)
    {
        var result = _validator.Validate(Parse(sampleFileName));

        Assert.True(result.IsValid, $"{sampleFileName} should be valid. Failures: {Describe(result)}");
    }

    /// <summary>
    /// The invalid fixture exists to be rejected by validation rather than by parsing, and to break
    /// more than one rule so multi-failure reporting is exercised. This is the assertion behind the
    /// list in samples/README.md.
    /// </summary>
    [Fact]
    public void Invalid_sample_breaks_exactly_the_documented_rules()
    {
        var result = _validator.Validate(Parse(Sample.Invalid));

        Assert.Equal(
            [
                new ValidationFailure("occurredAtUtc", ValidationRule.NotUtc),
                new ValidationFailure("correlationId", ValidationRule.Required),
                new ValidationFailure("data.orderId", ValidationRule.Required),
                new ValidationFailure("data.currency", ValidationRule.NotACurrencyCode),
                new ValidationFailure("data.amountMinor", ValidationRule.NotPositive),
            ],
            result.Failures);
    }

    private OrderCreatedV1 Parse(string sampleFileName) =>
        Assert.IsType<ParseResult.Parsed>(_parser.Parse(Sample.Read(sampleFileName))).Event;

    private static string Describe(ValidationResult result) =>
        string.Join(", ", result.Failures.Select(failure => $"{failure.Field}:{failure.Rule}"));
}
