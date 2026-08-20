namespace ReliableOrders.CdkTests;

/// <summary>
/// Reads a file out of the working tree, for the cases whose subject is a file rather than a
/// template.
/// </summary>
/// <remarks>
/// The file is not copied beside the test assembly, and copying it would defeat the point — an
/// assertion about the Compose file has to read the one <c>docker compose</c> actually runs.
/// </remarks>
internal static class RepositoryFiles
{
    /// <summary>
    /// The repository root, found by walking up to the solution file.
    /// </summary>
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

    /// <summary>
    /// Reads a file named relative to <see cref="Root"/>.
    /// </summary>
    /// <param name="relativePath">Where the file is, in repository terms.</param>
    public static string Read(string relativePath)
    {
        var path = Path.Combine(Root, relativePath);

        Assert.True(File.Exists(path), $"Expected {relativePath} at {path}.");

        return File.ReadAllText(path);
    }

    /// <summary>What identifies the repository root.</summary>
    private const string SolutionFile = "ReliableOrders.slnx";
}
