namespace ReliableOrders.UnitTests;

/// <summary>
/// Where the working tree is, for the cases whose subject is a file rather than a type.
/// </summary>
/// <remarks>
/// Shared rather than repeated per class. Both audits here read documents out of the repository, and
/// two copies of the same walk mean a rename of the solution file is a search rather than an edit.
/// </remarks>
internal static class Repository
{
    /// <summary>The repository root, found by walking up to the solution file.</summary>
    public static string Root
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, SolutionFile)))
            {
                directory = directory.Parent;
            }

            Assert.NotNull(directory);

            return directory.FullName;
        }
    }

    /// <summary>Every markdown file in the working tree, excluding what is not ours.</summary>
    /// <remarks>
    /// The CDK CLI ships markdown under <c>node_modules</c>, and a link audit that read it would
    /// report on documents nobody in this repository wrote.
    /// </remarks>
    public static IEnumerable<string> MarkdownFiles() =>
        Directory.EnumerateFiles(Root, "*.md", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal);

    /// <summary>What identifies the repository root.</summary>
    private const string SolutionFile = "ReliableOrders.slnx";
}
