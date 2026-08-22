using System.Text.RegularExpressions;

namespace ReliableOrders.UnitTests;

/// <summary>
/// Audits the architecture decision records against the template they claim to share.
/// </summary>
/// <remarks>
/// <para>
/// The template is a file rather than a convention, so nothing enforces it: a record written from
/// memory, or one that grew a section its neighbours do not have, reads as deliberate. What that
/// costs is not tidiness — a reader who finds "Alternatives considered" in four records and not the
/// fifth cannot tell whether the alternatives were weighed and omitted or never weighed at all.
/// </para>
/// <para>
/// What this can check is structure and reachability. Whether a record's reasoning is any good stays
/// a review judgement, and naming that limit is the point rather than letting the test's existence
/// imply more than it verifies.
/// </para>
/// </remarks>
public sealed partial class ArchitectureDecisionTests
{
    /// <summary>
    /// Every record carries the template's sections, in the template's order.
    /// </summary>
    /// <remarks>
    /// Read from the template rather than written down here, so changing the shape of a record means
    /// changing the file every record is copied from — which is the only edit that could keep them in
    /// step.
    /// </remarks>
    [Fact]
    public void Every_record_follows_the_template()
    {
        var expected = Sections(Read("template.md"));

        Assert.NotEmpty(expected);

        foreach (var record in Records())
        {
            Assert.Equal<IEnumerable<string>>(expected, Sections(Read(record)));
        }
    }

    /// <summary>
    /// The numbered records run from 0001 without a gap, and no number is reused.
    /// </summary>
    /// <remarks>
    /// Records are referenced by number — the template says a number is never reused, and ADR 0002
    /// cites 0005 by it. A gap is the visible half of that: it means either a record was deleted,
    /// which breaks whatever cited it, or one was never written and the sequence is quietly claiming
    /// otherwise.
    /// </remarks>
    [Fact]
    public void The_records_are_numbered_without_a_gap()
    {
        var numbers = new List<int>();

        foreach (var record in Records())
        {
            var number = Number().Match(record);

            Assert.True(
                number.Success,
                $"'{record}' does not begin with a four-digit number, so it cannot be cited as one. "
                + "Name it NNNN-short-kebab-title.md, as the template says.");

            numbers.Add(int.Parse(
                number.Groups["number"].Value,
                System.Globalization.CultureInfo.InvariantCulture));
        }

        numbers.Sort();

        Assert.Equal(Enumerable.Range(1, numbers.Count), numbers);
    }

    /// <summary>
    /// Each record is reachable from the README, which is where a reader starts.
    /// </summary>
    /// <remarks>
    /// A record nobody links to is a record nobody reads, and the decision it holds gets re-argued in
    /// a pull request instead — which is the failure the whole directory exists to prevent.
    /// </remarks>
    [Fact]
    public void Every_record_is_linked_from_the_readme()
    {
        var readme = File.ReadAllText(Path.Combine(Repository.Root, "README.md"));

        var unlinked = Records()
            .Where(record => !readme.Contains($"docs/adr/{record}", StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            unlinked.Length == 0,
            $"The README links to none of: {string.Join(", ", unlinked)}. A record nobody can reach "
            + "is a decision that gets re-argued rather than read.");
    }

    /// <summary>
    /// Every record cited anywhere in the repository exists.
    /// </summary>
    /// <remarks>
    /// The reverse of the case above, and the half that catches a deletion. Removing the
    /// highest-numbered record leaves the sequence unbroken — four records still run from 0001 — while
    /// the README, the documentation index, another record and a runbook all keep pointing at it.
    /// Records are cited by number precisely because they are stable, so a citation that no longer
    /// resolves is the decision having been quietly withdrawn.
    /// </remarks>
    [Fact]
    public void Every_record_that_is_cited_exists()
    {
        var missing = new List<string>();

        foreach (var document in Repository.MarkdownFiles())
        {
            // Resolved against the citing document rather than against the record directory, because
            // a record citing its neighbour writes the bare file name. Matching only `adr/…` would
            // have read every sibling citation as absent — and it is the records that cite each other
            // most.
            var directory = Path.GetDirectoryName(document) ?? Repository.Root;

            foreach (var citation in Citation().Matches(File.ReadAllText(document)).Cast<Match>())
            {
                var target = citation.Groups["target"].Value;

                if (!File.Exists(Path.GetFullPath(Path.Combine(directory, target))))
                {
                    missing.Add(
                        $"{Path.GetRelativePath(Repository.Root, document)} cites {target}");
                }
            }
        }

        Assert.True(
            missing.Count == 0,
            $"These citations resolve to nothing: {string.Join(", ", missing)}. A record is cited by "
            + "number because the number is stable, so a broken citation is a decision withdrawn "
            + "without anyone saying so.");
    }

    /// <summary>
    /// Every record says what became of the decision, in the words the template offers.
    /// </summary>
    /// <remarks>
    /// The status is what separates a record from a proposal. A record whose status is missing, or is
    /// the template's own commentary left in place, claims nothing at all — and the one thing a
    /// reader needs before weighing a decision is whether it is still in force.
    /// </remarks>
    [Fact]
    public void Every_record_declares_a_status()
    {
        foreach (var record in Records())
        {
            var status = Status().Match(Read(record));

            Assert.True(status.Success, $"{record} declares no status.");

            var declared = status.Groups["status"].Value.Trim();

            Assert.True(
                declared.StartsWith("Accepted", StringComparison.Ordinal)
                || declared.StartsWith("Proposed", StringComparison.Ordinal)
                || declared.StartsWith("Superseded", StringComparison.Ordinal)
                || declared.StartsWith("Deprecated", StringComparison.Ordinal),
                $"{record} declares its status as '{declared}', which is not one the template offers.");
        }
    }

    /// <summary>
    /// The numbered records, by file name.
    /// </summary>
    /// <remarks>
    /// Asserted non-empty here rather than in each case, because every case below is a loop over this
    /// and a loop over nothing passes. A rename that put the records outside this pattern — a prefix,
    /// a subdirectory — would otherwise leave four green tests auditing no records at all, which is
    /// the failure <c>TestPlanAuditTests</c> guards against for the same reason.
    /// </remarks>
    private static string[] Records()
    {
        var records = System.IO.Directory.EnumerateFiles(RecordDirectory(), "0*.md")
            .Select(Path.GetFileName)
            .OfType<string>()
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            records.Length > 0,
            $"No architecture decision records were found in {RecordDirectory()}. Every case here "
            + "loops over them, so finding none is four tests passing over nothing.");

        return records;
    }

    private static string Read(string name) => File.ReadAllText(Path.Combine(RecordDirectory(), name));

    private static string RecordDirectory() => Path.Combine(Repository.Root, "docs", "adr");

    /// <summary>The second-level headings, which is what a record's shape is.</summary>
    private static IReadOnlyList<string> Sections(string record) =>
        [.. Section().Matches(record).Select(match => match.Groups["name"].Value.Trim())];

    [GeneratedRegex(@"^## (?<name>.+)$", RegexOptions.Multiline)]
    private static partial Regex Section();

    /// <summary>The number a record's file name begins with.</summary>
    [GeneratedRegex(@"^(?<number>\d{4})-")]
    private static partial Regex Number();

    /// <summary>
    /// A markdown link whose target is a numbered record, wherever the citing document sits.
    /// </summary>
    /// <remarks>
    /// The whole target is captured rather than the file name, so it can be resolved against the
    /// document that wrote it. A link is matched by the shape of what it points at — four digits, a
    /// hyphen, a markdown file — which is what a record is named, and is why a citation to one cannot
    /// be confused with a link to anything else in the repository.
    /// </remarks>
    [GeneratedRegex(@"\]\((?<target>[^)\s]*\d{4}-[\w.-]+\.md)\)")]
    private static partial Regex Citation();

    /// <summary>
    /// The first line of prose under the status heading, skipping the template's comments.
    /// </summary>
    [GeneratedRegex(@"## Status\s*(<!--.*?-->\s*)?(?<status>[^\r\n<][^\r\n]*)", RegexOptions.Singleline)]
    private static partial Regex Status();
}
