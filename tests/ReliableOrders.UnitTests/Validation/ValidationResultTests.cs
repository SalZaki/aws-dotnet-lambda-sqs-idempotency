using ReliableOrders.Core.Validation;

namespace ReliableOrders.UnitTests.Validation;

public sealed class ValidationResultTests
{
    private static readonly ValidationFailure AnyFailure = new("data.orderId", ValidationRule.Required);
    private static readonly ValidationFailure OtherFailure = new("source", ValidationRule.TooLong);

    /// <summary>
    /// The compiler-generated equality compares the list by reference, which makes two results
    /// describing identical problems unequal while two empty ones compare equal by array interning.
    /// </summary>
    [Fact]
    public void Results_with_the_same_failures_are_equal()
    {
        Assert.Equal(new ValidationResult([AnyFailure]), new ValidationResult([AnyFailure]));
    }

    [Fact]
    public void Results_with_different_failures_are_not_equal()
    {
        Assert.NotEqual(new ValidationResult([AnyFailure]), new ValidationResult([OtherFailure]));
    }

    [Fact]
    public void Order_is_part_of_equality()
    {
        Assert.NotEqual(
            new ValidationResult([AnyFailure, OtherFailure]),
            new ValidationResult([OtherFailure, AnyFailure]));
    }

    [Fact]
    public void Equal_results_share_a_hash_code()
    {
        Assert.Equal(
            new ValidationResult([AnyFailure, OtherFailure]).GetHashCode(),
            new ValidationResult([AnyFailure, OtherFailure]).GetHashCode());
    }

    [Fact]
    public void Valid_equals_any_empty_result()
    {
        Assert.Equal(ValidationResult.Valid, new ValidationResult([]));
        Assert.True(ValidationResult.Valid.IsValid);
    }

    [Fact]
    public void A_result_with_failures_is_not_valid()
    {
        Assert.False(new ValidationResult([AnyFailure]).IsValid);
    }

    /// <summary>
    /// The validator builds its failures in a mutable list. Handing that list out by reference would
    /// let a caller edit a result after the fact.
    /// </summary>
    [Fact]
    public void Failures_are_copied_away_from_the_caller_list()
    {
        var source = new List<ValidationFailure> { AnyFailure };
        var result = new ValidationResult(source);

        source.Add(OtherFailure);

        Assert.Single(result.Failures);
    }
}
