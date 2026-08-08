using System.Globalization;
using System.Text.RegularExpressions;

namespace ReliableOrders.UnitTests;

/// <summary>
/// Holds the Coverage of the required cases table in docs/testing-strategy.md against the suite.
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
    /// Every case in the plan is accounted for, either by a test or by what it waits on.
    /// </summary>
    /// <remarks>
    /// Ten of the thirty describe components that do not exist yet. Listing them as outstanding is what
    /// keeps them from being forgotten, so the presence of every row is asserted rather than its text.
    /// </remarks>
    [Fact]
    public void The_plan_accounts_for_all_thirty_required_cases()
    {
        var rows = CaseRow().Matches(ReadTestingStrategy())
            .Select(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture))
            .ToArray();

        Assert.Equal(Enumerable.Range(1, 30), rows);
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

    /// <summary>Matches a test class declaration.</summary>
    [GeneratedRegex(@"class\s+([A-Za-z0-9_]+Tests)\b")]
    private static partial Regex TestClassDeclaration();
}
