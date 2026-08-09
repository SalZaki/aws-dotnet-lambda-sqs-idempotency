using ReliableOrders.Aws.Sqs;

namespace ReliableOrders.UnitTests.Processing;

/// <summary>
/// How remaining invocation time becomes the instant record processing must stop.
/// </summary>
/// <remarks>
/// Worth its own suite because the handler tests bypass it: they construct a
/// <c>BatchInvocation</c> with an absolute deadline so each can place it exactly where the test
/// needs. That leaves the arithmetic here — and its boundary, where the margin has already been
/// spent — exercised by nothing else.
/// </remarks>
public sealed class ProcessingDeadlineTests
{
    [Fact]
    public void The_deadline_is_the_remaining_time_less_the_margin()
    {
        var deadline = ProcessingDeadline.From(Now, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(2));

        Assert.Equal(Now.AddSeconds(28), deadline);
    }

    /// <summary>
    /// The margin defaults rather than being optional in effect.
    /// </summary>
    /// <remarks>
    /// Omitting it must apply <see cref="ProcessingDeadline.DefaultMargin"/>, not zero. A zero margin
    /// would let a record start with no time to finish, which is the failure the margin exists to
    /// prevent.
    /// </remarks>
    [Fact]
    public void An_omitted_margin_uses_the_default()
    {
        var deadline = ProcessingDeadline.From(Now, TimeSpan.FromSeconds(30));

        Assert.Equal(Now + TimeSpan.FromSeconds(30) - ProcessingDeadline.DefaultMargin, deadline);
        Assert.NotEqual(Now.AddSeconds(30), deadline);
    }

    /// <summary>
    /// Remaining time already inside the margin yields a deadline in the past.
    /// </summary>
    /// <remarks>
    /// The documented boundary, and the intended reading rather than an accident: there is no time to
    /// finish a record, so every record is deferred, the invocation returns what it has, and SQS
    /// redelivers the rest. The handler defers when <c>now &gt;= deadline</c>, so a deadline at or
    /// before <c>now</c> defers everything.
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(500)]
    [InlineData(1_999)]
    [InlineData(2_000)]
    public void Remaining_time_within_the_margin_yields_a_deadline_already_passed(int remainingMs)
    {
        var deadline = ProcessingDeadline.From(
            Now,
            TimeSpan.FromMilliseconds(remainingMs),
            TimeSpan.FromSeconds(2));

        Assert.True(Now >= deadline, $"{remainingMs}ms remaining should defer every record");
    }

    /// <summary>
    /// A millisecond past the margin is a deadline in the future.
    /// </summary>
    /// <remarks>
    /// The other side of the same boundary. Without it the test above would pass on a method that
    /// always returned a past instant.
    /// </remarks>
    [Fact]
    public void Remaining_time_beyond_the_margin_yields_a_deadline_still_ahead()
    {
        var deadline = ProcessingDeadline.From(
            Now,
            TimeSpan.FromMilliseconds(2_001),
            TimeSpan.FromSeconds(2));

        Assert.True(deadline > Now);
    }

    /// <summary>
    /// The default is a real margin, not a placeholder that happens to be zero.
    /// </summary>
    /// <remarks>
    /// It is provisional — the specification asks for an observed p99 and nothing has run yet — so
    /// this pins only that it holds time back. Replacing the value is expected; replacing it with
    /// nothing is not.
    /// </remarks>
    [Fact]
    public void The_default_margin_holds_time_back()
    {
        Assert.True(ProcessingDeadline.DefaultMargin > TimeSpan.Zero);
    }

    private static readonly DateTimeOffset Now = new(2026, 8, 9, 12, 0, 0, TimeSpan.Zero);
}
