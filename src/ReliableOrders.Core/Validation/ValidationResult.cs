namespace ReliableOrders.Core.Validation;

/// <summary>
/// Every rule an event broke, or none.
/// </summary>
/// <remarks>
/// <para>
/// Validation reports all failures rather than stopping at the first. A publisher fixing one field
/// per redelivery would otherwise take as many round trips as it has mistakes, and each one costs a
/// dead-lettered message.
/// </para>
/// <para>
/// Failures arrive in a fixed order: envelope fields in contract order, then <c>data</c> fields in
/// contract order. The order is part of what this type promises, so a logged failure list reads the
/// same way every time and can be compared between two runs.
/// </para>
/// </remarks>
/// <param name="Failures">Empty when the event is valid.</param>
public sealed record ValidationResult(IReadOnlyList<ValidationFailure> Failures)
{
    /// <summary>
    /// Every rule the event broke, in the order described above.
    /// </summary>
    /// <remarks>
    /// Copied on construction. The validator builds its failures in a mutable list, and handing that
    /// list out by reference would let a caller edit a result after the fact.
    /// </remarks>
    public IReadOnlyList<ValidationFailure> Failures { get; } = [.. Failures];

    /// <summary>
    /// The shared result for an event that broke no rules.
    /// </summary>
    public static ValidationResult Valid { get; } = new([]);

    /// <summary>
    /// True when the event broke no rules.
    /// </summary>
    public bool IsValid => Failures.Count == 0;

    /// <summary>
    /// Compares the failures themselves rather than the list holding them.
    /// </summary>
    /// <remarks>
    /// The compiler-generated version compares <see cref="Failures"/> by reference, which makes two
    /// results describing identical problems unequal, while two empty results compare equal because
    /// an empty collection expression yields the interned empty array. Equality that depends on
    /// whether the list happens to be empty is worse than no equality at all.
    /// </remarks>
    public bool Equals(ValidationResult? other) =>
        other is not null && Failures.SequenceEqual(other.Failures);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = default(HashCode);

        foreach (var failure in Failures)
        {
            hash.Add(failure);
        }

        return hash.ToHashCode();
    }
}
