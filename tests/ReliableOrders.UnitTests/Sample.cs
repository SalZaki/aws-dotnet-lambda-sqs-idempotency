namespace ReliableOrders.UnitTests;

/// <summary>
/// Reads the fixtures in <c>samples/</c>, which the build copies beside the test assembly.
/// </summary>
internal static class Sample
{
    internal const string Valid = "valid-order-created-v1.json";
    internal const string Duplicate = "duplicate-order-created-v1.json";
    internal const string Republished = "republished-order-created-v1.json";
    internal const string Conflicting = "conflicting-order-created-v1.json";
    internal const string Invalid = "invalid-order-created-v1.json";

    internal static string Read(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Samples", fileName);

        Assert.True(
            File.Exists(path),
            $"Missing sample fixture {fileName}. Expected it at {path}, copied there by the None "
            + "Include in ReliableOrders.UnitTests.csproj.");

        return File.ReadAllText(path);
    }
}
