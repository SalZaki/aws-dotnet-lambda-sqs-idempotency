using System.Text.Json;

namespace ReliableOrders.UnitTests.Idempotency;

/// <summary>
/// One committed known-answer vector: a wire event and the hashes this repository has promised to
/// produce for it.
/// </summary>
/// <param name="Name">Identifies the vector in test output and in the file.</param>
/// <param name="Why">What the vector exists to catch, carried alongside it so the file explains itself.</param>
/// <param name="EnvelopeSha256">The committed envelope hash, lowercase hexadecimal.</param>
/// <param name="BusinessSha256">The committed business hash, lowercase hexadecimal.</param>
/// <param name="Body">
/// The event exactly as it appears in the file, so the vector goes through the parser rather than
/// being rebuilt by the test.
/// </param>
internal sealed record HashVector(
    string Name,
    string Why,
    string EnvelopeSha256,
    string BusinessSha256,
    string Body);

/// <summary>
/// Reads <c>Vectors/hash-vectors.json</c>, which the build copies beside the test assembly.
/// </summary>
internal static class HashVectors
{
    internal const string Reference = "reference";
    internal const string CausedEvent = "caused-event";
    internal const string UnknownTopLevelFields = "unknown-top-level-fields";
    internal const string SameDataNewEventId = "same-data-new-event-id";

    internal static IReadOnlyList<HashVector> All { get; } = Load();

    internal static HashVector Named(string name) =>
        All.SingleOrDefault(vector => string.Equals(vector.Name, name, StringComparison.Ordinal))
        ?? throw new InvalidOperationException($"No vector named '{name}' in {FileName}.");

    private const string FileName = "hash-vectors.json";

    private static HashVector[] Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Vectors", FileName);

        Assert.True(
            File.Exists(path),
            $"Missing {FileName}. Expected it at {path}, copied there by the None Update in "
            + "ReliableOrders.UnitTests.csproj. Without it the vectors are unverified rather than "
            + "satisfied.");

        using var document = JsonDocument.Parse(File.ReadAllText(path));

        var vectors = document.RootElement.GetProperty("vectors").EnumerateArray().Select(vector =>
            new HashVector(
                Name: vector.GetProperty("name").GetString()!,
                Why: vector.GetProperty("why").GetString()!,
                EnvelopeSha256: vector.GetProperty("envelopeSha256").GetString()!,
                BusinessSha256: vector.GetProperty("businessSha256").GetString()!,
                Body: vector.GetProperty("event").GetRawText()))
            .ToArray();

        Assert.NotEmpty(vectors);

        return vectors;
    }
}
