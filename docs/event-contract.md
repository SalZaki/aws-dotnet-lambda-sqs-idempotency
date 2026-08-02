# Event Contract

Use a versioned envelope so transport and domain metadata do not become mixed together.

```json
{
  "schemaVersion": 1,
  "eventId": "0d76e91c-44e6-4fba-901f-bfdb76645299",
  "eventType": "order.created",
  "occurredAtUtc": "2026-08-01T10:30:00Z",
  "source": "sample.order-publisher",
  "correlationId": "f1e02471-f9da-437f-bc32-e4e65394658a",
  "causationId": null,
  "data": {
    "orderId": "ORD-100001",
    "customerId": "CUS-90001",
    "currency": "GBP",
    "amountMinor": 1299,
    "itemDescription": "Mechanical keyboard"
  }
}
```

The contract types are the shape every other component signature refers to.

```csharp
public sealed record OrderCreatedV1(
    int SchemaVersion,
    Guid EventId,
    string EventType,
    DateTimeOffset OccurredAtUtc,
    string Source,
    Guid CorrelationId,
    Guid? CausationId,
    OrderData Data);

public sealed record OrderData(
    string OrderId,
    string CustomerId,
    string Currency,
    long AmountMinor,
    string ItemDescription);
```

`AmountMinor` is `long` rather than `int` so a high-precision minor unit on a large order cannot
overflow. `CausationId` is nullable because a root event has no cause.

## Contract Rules

- `schemaVersion` is required and must currently equal `1`.
- `eventId` is required and must be a valid UUID. Its canonical string form is 36 characters, a
  length the [Transaction Requests Must Be
  Deterministic](correctness-model.md#transaction-requests-must-be-deterministic) section relies on.
- `eventType` must equal `order.created`.
- `occurredAtUtc` must be a UTC instant. After deserialization to `DateTimeOffset`, `Offset` must
  equal `TimeSpan.Zero`. A value with a non-zero offset is rejected rather than normalised, because
  normalising would change the hash input.
- `occurredAtUtc` must fall within a bounded skew window of processing time. The recommended bounds
  are not more than 24 hours in the future and not more than the source queue retention plus 1 day
  in the past. Both bounds are configurable. This is a validation rule, not a correctness mechanism,
  and uses `TimeProvider`.
- `source` is required and length-limited.
- `correlationId` is required.
- `causationId` is optional and may be null. It is carried inside `EnvelopeSha256`, so two events
  identical except for `causationId` are distinct events, not duplicates.
- `orderId` and `customerId` are required and length-limited.
- `currency` is a three-letter uppercase currency code.
- `amountMinor` is a positive integer in the currency's minor unit.
- `itemDescription` is required and length-limited. Field limits must keep the worst-case DynamoDB
  item well under the 400 KB item-size ceiling.
- Unknown top-level fields are tolerated for forward compatibility.
- Unknown schema versions must not be silently processed.

Using an integer minor-unit amount avoids floating-point ambiguity.

**Consequence of tolerating unknown fields.** Unknown fields are dropped during canonicalisation and
therefore do not contribute to either hash. Two events differing only in fields this schema version
does not know about hash identically and are classified as duplicates of one another. This is
intended — a v1 processor has no basis on which to treat unrecognised data as business-significant —
but it is load-bearing and must be stated in `docs/correctness-model.md` and covered by a test.

## Idempotency Keys

Two safeguards operate at different scopes, each with its own hash. The Two Idempotency Scopes
Require Two Hashes section carries the full mapping.

- The event-level key is `eventId`, compared on `EnvelopeSha256`.
- The domain-level key is `orderId`, compared on `BusinessSha256`.

A repeated `eventId` with the same envelope hash is a duplicate.

A new `eventId` attempting to create an existing `orderId` is one of two things.

- a duplicate logical order when `BusinessSha256` matches the stored order; or
- a conflict when it differs.

## Canonical Representation and Hashing

Both hashes must be deterministic across machines, processes, and .NET versions.

1. Map the deserialized event into an explicit canonical internal representation. Do not hash the
   deserialized contract types directly; a canonical type makes the hash input a deliberate,
   reviewable decision.
2. Serialize with a dedicated source-generated `System.Text.Json` context that fixes property order,
   uses invariant number and string formatting, writes `occurredAtUtc` in round-trip UTC form
   (`"O"`), and does not emit indentation.
3. Hash the UTF-8 bytes with SHA-256.
4. Store the lowercase hexadecimal hash.

`EnvelopeSha256` hashes the canonical envelope. `BusinessSha256` hashes the canonical `data` object
alone. The `data` canonicalisation used for `BusinessSha256` must be the identical routine nested
inside the envelope canonicalisation, so the two can never drift.

Do not hash the raw SQS body — insignificant JSON formatting differences would create different
hashes.

### Known-answer vectors

Determinism across .NET versions cannot be tested by comparing two hashes computed in the same
process, because both move together when the serializer changes. Commit known-answer vectors
instead.

- Store fixed sample events under `tests/ReliableOrders.UnitTests/Vectors/` together with their
  expected lowercase hexadecimal `EnvelopeSha256` and `BusinessSha256`.
- Cover at least a minimal event, an event with a null `causationId`, an event carrying unknown
  top-level fields, and an event whose `data` matches another vector under a different `eventId`.
- Assert the computed hashes equal the committed constants.

A vector test failing after an SDK or runtime upgrade is the intended signal. It means
canonicalisation changed, and every idempotency record written before the upgrade now hashes
differently, which would reclassify replays as conflicts. Treat a vector change as a schema
migration, never as a test to update.
