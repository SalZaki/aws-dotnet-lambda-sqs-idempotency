namespace ReliableOrders.IntegrationTests;

/// <summary>
/// The trait that keeps container-backed tests out of the pull-request build gate.
/// </summary>
/// <remarks>
/// <para>
/// The gate runs <c>--filter-not-trait "Category=Integration"</c>, so a test carrying this trait is
/// left out there and runs in the integration workflow instead. The filter is expressed as an
/// exclusion rather than an inclusion on purpose: a new test project that nobody remembers to wire up
/// still runs in the gate, which is the safe direction to be wrong in.
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
    /// The trait name, which is the left half of every <c>--filter-trait</c> pair.
    /// </summary>
    internal const string Name = "Category";

    /// <summary>
    /// Requires a reachable Docker daemon.
    /// </summary>
    internal const string Integration = "Integration";

    /// <summary>
    /// Additionally requires a LocalStack auth token, and therefore outbound network.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Carried as a second <c>Category</c> value alongside <see cref="Integration"/>, so the
    /// integration workflow can run everything with <c>--filter-trait "Category=Integration"</c> when
    /// a token is available and add <c>--filter-not-trait "Category=RequiresLocalStackToken"</c> when
    /// there is none. The two combine as an AND, where the VSTest expression they replace joined the
    /// same two conditions with <c>&amp;</c>.
    /// </para>
    /// <para>
    /// It exists because of who cannot supply one. GitHub does not expose repository secrets to a
    /// pull request from a fork, so an outside contributor's run has no token however the repository
    /// is configured. Without this split their run would fail on the container rather than on their
    /// change; with it, the transaction tests still run and the workflow says plainly which tests were
    /// left out.
    /// </para>
    /// </remarks>
    internal const string RequiresLocalStackToken = "RequiresLocalStackToken";
}
