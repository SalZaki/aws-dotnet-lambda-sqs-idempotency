using System.Globalization;
using System.Text.RegularExpressions;

namespace ReliableOrders.UnitTests;

/// <summary>
/// Audits the coverage table in docs/testing-strategy.md against the tests that exist.
/// </summary>
/// <remarks>
/// <para>
/// The table names a test class per case, by hand. Nothing in the compiler connects the two, so a
/// rename or a deletion would leave the plan quietly claiming coverage that no longer exists — and a
/// test plan that is wrong is worse than one that is merely incomplete, because it is still trusted.
/// </para>
/// <para>
/// What this can check is that every class the table names exists. It cannot check that the class
/// still covers what the row claims; that stays a review judgement. Naming the limit is the point,
/// rather than letting the test's existence imply more than it verifies.
/// </para>
/// </remarks>
public sealed partial class TestPlanAuditTests
{
    [Fact]
    public void Every_test_class_the_plan_names_exists()
    {
        var named = NamedTestClasses();

        Assert.NotEmpty(named);

        var known = KnownTestClassNames();
        var missing = named.Where(name => !known.Contains(name)).Order(StringComparer.Ordinal).ToArray();

        Assert.True(
            missing.Length == 0,
            $"docs/testing-strategy.md names test classes that no longer exist: {string.Join(", ", missing)}. "
            + "Rename the row alongside the class, or say what replaced it. A plan claiming coverage "
            + "that is not there reads as done rather than as missing.");
    }

    /// <summary>
    /// Every required case has a row in the coverage table, and the table invents none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both sides are read from the document, so adding a case needs no edit here. A count written
    /// into this test was worse than no test on the day a case was added: the failure named the
    /// number rather than the missing row, and the cheapest way to green was to raise the number.
    /// </para>
    /// <para>
    /// What it catches is a case listed as required and then not carried into the table — a plan that
    /// silently stops tracking something it asked for. Whether the named class actually covers what
    /// the row claims stays a review judgement, as the case above says.
    /// </para>
    /// </remarks>
    [Fact]
    public void Every_required_case_has_a_coverage_row()
    {
        var plan = ReadTestingStrategy();

        var required = Numbers(RequiredCase().Matches(UnitTestCases(plan)));
        var covered = Numbers(CaseRow().Matches(plan));

        Assert.NotEmpty(required);
        Assert.Equal(Enumerable.Range(1, required.Length), required);
        Assert.Equal(required, covered);
    }

    private static int[] Numbers(MatchCollection matches) =>
        [.. matches.Select(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture))];

    /// <summary>
    /// The unit tests' required-cases list, and nothing after it.
    /// </summary>
    /// <remarks>
    /// Concurrency tests and the real-AWS scenarios number their own lists from one, and the coverage
    /// table covers neither. Matching numbered lines across the whole document would count all three
    /// as required cases and compare a run of forty-odd against a table of thirty-six.
    /// </remarks>
    private static string UnitTestCases(string plan)
    {
        const string heading = "### Required cases";

        var start = plan.IndexOf(heading, StringComparison.Ordinal);

        Assert.True(start >= 0, $"Expected '{heading}' under Unit Tests in the test plan.");

        var end = plan.IndexOf("###", start + heading.Length, StringComparison.Ordinal);

        return end < 0 ? plan[start..] : plan[start..end];
    }

    private static string[] NamedTestClasses() =>
        [.. TestClassReference().Matches(ReadTestingStrategy())
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)];

    /// <summary>
    /// Every test class declared anywhere under <c>tests/</c>.
    /// </summary>
    /// <remarks>
    /// Read from the sources rather than from loaded assemblies. The integration tests build to their
    /// own output directory, so an assembly scan from here cannot see them, and referencing that
    /// project to make it possible would drag Testcontainers into the gate for no other reason.
    /// </remarks>
    private static HashSet<string> KnownTestClassNames()
    {
        var generated = $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}";
        var output = $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}";

        var names = Directory
            .EnumerateFiles(Path.Combine(RepositoryRoot(), "tests"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains(generated, StringComparison.Ordinal))
            .Where(path => !path.Contains(output, StringComparison.Ordinal))
            .SelectMany(path => TestClassDeclaration().Matches(File.ReadAllText(path)))
            .Select(match => match.Groups[1].Value);

        return new HashSet<string>(names, StringComparer.Ordinal);
    }

    private static string ReadTestingStrategy()
    {
        var path = Path.Combine(RepositoryRoot(), "docs", "testing-strategy.md");

        Assert.True(File.Exists(path), $"Expected the test plan at {path}.");

        return File.ReadAllText(path);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ReliableOrders.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return directory.FullName;
    }

    /// <summary>Matches a backtick-quoted identifier ending in Tests.</summary>
    [GeneratedRegex("`([A-Za-z0-9_]+Tests)`")]
    private static partial Regex TestClassReference();

    /// <summary>Matches the case number at the start of a coverage table row.</summary>
    [GeneratedRegex(@"^\| (\d+) \| ", RegexOptions.Multiline)]
    private static partial Regex CaseRow();

    /// <summary>
    /// Matches a numbered case in the required-cases list.
    /// </summary>
    /// <remarks>
    /// Anchored to the start of a line so a number appearing inside a case's prose is not read as
    /// another case. Only the unit-test list is numbered this way; the other sections use bullets.
    /// </remarks>
    [GeneratedRegex(@"^(\d+)\. ", RegexOptions.Multiline)]
    private static partial Regex RequiredCase();

    /// <summary>Matches a test class declaration.</summary>
    [GeneratedRegex(@"class\s+([A-Za-z0-9_]+Tests)\b")]
    private static partial Regex TestClassDeclaration();
}
