namespace ReliableOrders.IntegrationTests;

/// <summary>
/// The trait that keeps container-backed tests out of the pull-request build gate.
/// </summary>
/// <remarks>
/// <para>
/// The gate runs <c>--filter "Category!=Integration"</c>, so a test carrying this trait is skipped
/// there and runs in the integration workflow instead. The filter is expressed as an exclusion rather
/// than an inclusion on purpose: a new test project that nobody remembers to wire up still runs in the
/// gate, which is the safe direction to be wrong in.
/// </para>
/// <para>
/// These tests are separated because they pull a 758 MB image and start a container, which is minutes
/// against a gate that otherwise finishes in well under one. They are not separated because they
/// matter less — classification correctness is what this project exists to demonstrate.
/// </para>
/// </remarks>
internal static class TestCategory
{
    /// <summary>
    /// The trait name. <c>Category</c> is what <c>dotnet test --filter</c> matches on.
    /// </summary>
    internal const string Name = "Category";

    /// <summary>
    /// Requires a reachable Docker daemon.
    /// </summary>
    internal const string Integration = "Integration";
}
