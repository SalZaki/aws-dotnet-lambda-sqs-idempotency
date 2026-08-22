using System.Runtime.CompilerServices;

namespace ReliableOrders.EndToEndTests;

/// <summary>
/// A fact that needs a deployed stack, and is skipped with a reason where there is none.
/// </summary>
/// <remarks>
/// The same shape as the integration suite's <c>RequiresLocalStackAttribute</c>, and for the same
/// reason: the condition, the type it is read from and the wording of the skip are one decision, and
/// spreading them over every test invites one of them to be written differently.
/// </remarks>
public sealed class RequiresDeploymentAttribute : FactAttribute
{
    /// <param name="sourceFilePath">Filled in by the compiler at the use site.</param>
    /// <param name="sourceLineNumber">Filled in by the compiler at the use site.</param>
    public RequiresDeploymentAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        Skip = Deployment.SkipReason;
        SkipType = typeof(Deployment);
        SkipUnless = nameof(Deployment.IsConfigured);
    }
}
