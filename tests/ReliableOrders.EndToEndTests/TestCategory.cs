namespace ReliableOrders.EndToEndTests;

/// <summary>
/// The trait that keeps tests needing a deployed stack out of the pull-request gate.
/// </summary>
/// <remarks>
/// <para>
/// The gate runs <c>--filter "Category!=Integration&amp;Category!=EndToEnd"</c>. That filter is an
/// exclusion so a new project nobody remembers to wire up still runs there, which is the safe
/// direction to be wrong in — and it stops being safe for a suite that reaches an AWS account. A
/// contributor's pull request would run these against credentials it can never have, and fail on
/// that rather than on their change.
/// </para>
/// <para>
/// The trait is how the gate declines to run them. <see cref="RequiresDeploymentAttribute"/> is
/// what one of them does when it runs anyway with nothing deployed, which is the case on a laptop.
/// </para>
/// </remarks>
internal static class TestCategory
{
    /// <summary>The trait name. <c>Category</c> is what <c>dotnet test --filter</c> matches on.</summary>
    internal const string Name = "Category";

    /// <summary>Requires a deployed stack, and credentials that may reach it.</summary>
    internal const string EndToEnd = "EndToEnd";
}
