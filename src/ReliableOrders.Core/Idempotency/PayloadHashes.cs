namespace ReliableOrders.Core.Idempotency;

/// <summary>
/// The two hashes one event is classified on. Specified in the Two Idempotency Scopes Require Two
/// Hashes section of docs/correctness-model.md.
/// </summary>
/// <remarks>
/// One hash cannot serve both scopes. A legitimate republish of the same order carries a new
/// <c>eventId</c> and a new <c>occurredAtUtc</c>, so its envelope hash necessarily differs from the
/// first event's. Classifying on the envelope hash alone would call every such republish a conflict
/// and route a valid order to the dead-letter queue with a high-severity alarm.
/// </remarks>
/// <param name="EnvelopeSha256">
/// Covers the whole canonical event, envelope and data together. Stored on the idempotency record and
/// answers "have I seen this exact event before?".
/// </param>
/// <param name="BusinessSha256">
/// Covers the canonical <c>data</c> object alone. Stored on the order item and answers "does this
/// order already exist with the same business data?".
/// </param>
public sealed record PayloadHashes(string EnvelopeSha256, string BusinessSha256);
