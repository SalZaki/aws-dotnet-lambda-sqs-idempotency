using ReliableOrders.Core.Persistence;

namespace ReliableOrders.UnitTests.Persistence;

/// <summary>
/// The classification vocabulary, and the exhaustiveness guarantee that stops a case being dropped.
/// </summary>
public sealed class OrderWriteResultTests
{
    /// <summary>
    /// Every case reaches its own handler and no other. Missing a case here would acknowledge a
    /// message that was never stored, so each one is asserted rather than sampled.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryCase))]
    public void Match_dispatches_to_the_handler_for_the_case(string caseName)
    {
        var result = Build(caseName);

        var dispatched = result.Match(
            whenCreated: _ => nameof(OrderWriteResult.Created),
            whenDuplicate: _ => nameof(OrderWriteResult.Duplicate),
            whenConflict: _ => nameof(OrderWriteResult.Conflict),
            whenTransientFault: _ => nameof(OrderWriteResult.TransientFault),
            whenPermanentFault: _ => nameof(OrderWriteResult.PermanentFault));

        Assert.Equal(caseName, dispatched);
    }

    /// <summary>
    /// The handler receives the case itself, not the base type, so a caller reads the scope or reason
    /// without a cast or a type test.
    /// </summary>
    [Fact]
    public void Match_hands_the_case_its_own_data()
    {
        OrderWriteResult duplicate = new OrderWriteResult.Duplicate(DuplicateScope.Order);
        OrderWriteResult conflict =
            new OrderWriteResult.Conflict(ConflictScope.TokenMismatch, WriteFailureReason.TokenMismatch);

        Assert.Equal(
            DuplicateScope.Order,
            duplicate.Match(_ => default, d => d.Scope, _ => default, _ => default, _ => default));

        Assert.Equal(
            ConflictScope.TokenMismatch,
            conflict.Match(_ => default, _ => default, c => c.Scope, _ => default, _ => default));
    }

    /// <summary>
    /// Two duplicates of different scope are different outcomes, and record equality has to say so.
    /// One is a redelivered message and the other a republished order.
    /// </summary>
    [Fact]
    public void Scope_takes_part_in_equality()
    {
        Assert.Equal(
            new OrderWriteResult.Duplicate(DuplicateScope.Event),
            new OrderWriteResult.Duplicate(DuplicateScope.Event));

        Assert.NotEqual(
            new OrderWriteResult.Duplicate(DuplicateScope.Event),
            new OrderWriteResult.Duplicate(DuplicateScope.Order));

        Assert.NotEqual(
            new OrderWriteResult.Conflict(ConflictScope.Event, WriteFailureReason.EnvelopeHashMismatch),
            new OrderWriteResult.Conflict(ConflictScope.Order, WriteFailureReason.EnvelopeHashMismatch));
    }

    /// <summary>
    /// The declared constructor is <c>private protected</c>, so no other assembly can name it to add a
    /// case.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Asserted by reflection rather than trusted to a comment, because the guarantee is one modifier
    /// that is easy to widen without noticing.
    /// </para>
    /// <para>
    /// The compiler-generated copy constructor is excluded, and it is worth saying why rather than
    /// quietly filtering it. Every non-sealed record gets a <c>protected</c> copy constructor, which
    /// another assembly could chain to. So "closed hierarchy" is a convention held up by the declared
    /// constructor, not a guarantee the runtime enforces — which is exactly why
    /// <see cref="OrderWriteResult.Match{TResult}"/> is what callers use. A rogue case would still have
    /// to implement <c>Match</c>, and could only answer as one of the cases named here.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_declared_constructor_is_private_protected()
    {
        var constructors = typeof(OrderWriteResult)
            .GetConstructors(
                System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Public)
            .Where(constructor => !IsCopyConstructor(constructor))
            .ToArray();

        var declared = Assert.Single(constructors);

        Assert.True(
            declared.IsFamilyAndAssembly,
            "OrderWriteResult's constructor is no longer private protected, so another assembly can add "
            + "a case that Match cannot see.");

        Assert.Empty(
            typeof(OrderWriteResult).GetConstructors(
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public));
    }

    private static bool IsCopyConstructor(System.Reflection.ConstructorInfo constructor)
    {
        var parameters = constructor.GetParameters();

        return parameters.Length == 1 && parameters[0].ParameterType == typeof(OrderWriteResult);
    }

    /// <summary>
    /// Every reason is namespaced by the classification it belongs to, so a reason cannot be attached
    /// to the wrong case without the string reading wrongly in the log that carries it.
    /// </summary>
    [Fact]
    public void Failure_reasons_are_low_cardinality_and_prefixed_by_classification()
    {
        var reasons = typeof(WriteFailureReason)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Select(field => (string)field.GetValue(null)!)
            .ToArray();

        Assert.NotEmpty(reasons);
        Assert.Equal(reasons.Length, reasons.Distinct(StringComparer.Ordinal).Count());

        Assert.All(reasons, reason => Assert.True(
            reason.StartsWith("conflict.", StringComparison.Ordinal)
            || reason.StartsWith("transient.", StringComparison.Ordinal)
            || reason.StartsWith("permanent.", StringComparison.Ordinal),
            $"'{reason}' does not name the classification it belongs to."));
    }

    public static TheoryData<string> EveryCase() =>
    [
        nameof(OrderWriteResult.Created),
        nameof(OrderWriteResult.Duplicate),
        nameof(OrderWriteResult.Conflict),
        nameof(OrderWriteResult.TransientFault),
        nameof(OrderWriteResult.PermanentFault),
    ];

    private static OrderWriteResult Build(string caseName) => caseName switch
    {
        nameof(OrderWriteResult.Created) => new OrderWriteResult.Created(),
        nameof(OrderWriteResult.Duplicate) => new OrderWriteResult.Duplicate(DuplicateScope.Event),
        nameof(OrderWriteResult.Conflict) =>
            new OrderWriteResult.Conflict(ConflictScope.Event, WriteFailureReason.EnvelopeHashMismatch),
        nameof(OrderWriteResult.TransientFault) =>
            new OrderWriteResult.TransientFault(WriteFailureReason.Throttled),
        nameof(OrderWriteResult.PermanentFault) =>
            new OrderWriteResult.PermanentFault(WriteFailureReason.MalformedRequest),
        _ => throw new ArgumentOutOfRangeException(nameof(caseName), caseName, "No case defined."),
    };
}
