using System.Runtime.CompilerServices;

namespace ReliableOrders.IntegrationTests;

/// <summary>
/// A fact that needs the SQS emulator, and is skipped with a reason on a machine that cannot start
/// one.
/// </summary>
/// <remarks>
/// <para>
/// An attribute rather than the three properties repeated on eight tests. The condition, the type it
/// is read from and the wording of the skip are one decision, and spreading them across every test
/// invites one of the eight to be written differently or a new one to be added without them.
/// </para>
/// <para>
/// It does not replace the <c>RequiresLocalStackToken</c> trait, which does a different job. This
/// decides what one test does when no token is present; the trait is how the workflow declines to run
/// them at all, before a two-gigabyte image is pulled for tests that would only skip.
/// </para>
/// </remarks>
public sealed class RequiresLocalStackAttribute : FactAttribute
{
    /// <summary>
    /// Marks the test as skippable, on the condition that the emulator can be started.
    /// </summary>
    /// <remarks>
    /// The two caller parameters carry the test's own file and line to the base attribute, which is
    /// what a runner reports a failure's location from. They are defaulted and never passed, and the
    /// compiler fills them in at each use site; omitting them would leave every test in these classes
    /// reporting this file as its source.
    /// </remarks>
    /// <param name="sourceFilePath">Filled in by the compiler at the use site.</param>
    /// <param name="sourceLineNumber">Filled in by the compiler at the use site.</param>
    public RequiresLocalStackAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        // Skip carries the reason and SkipUnless the condition: the reason is used only when the
        // property is false. Naming the type explicitly is what lets the condition live on the
        // fixture, which is the thing that knows, rather than being restated on each test class.
        Skip = LocalStackFixture.SkipReason;
        SkipType = typeof(LocalStackFixture);
        SkipUnless = nameof(LocalStackFixture.IsConfigured);
    }
}
