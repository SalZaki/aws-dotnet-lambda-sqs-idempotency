# ADR 0005 — Separate Envelope and Business Hashes

## Status

Accepted. Implemented by Story 1.3 in `src/ReliableOrders.Core/Idempotency/`.

## Context

The worker reads an SQS standard queue, so the same logical order can arrive more than once by two
different routes.

The first route is infrastructure. A standard queue may deliver one message several times, and
Lambda may retry a record after a timeout, a service error, a throttle or a partial-batch failure.
Every redelivery carries the same bytes, so the same `eventId`.

The second route is the publisher. An operator replaying a day's orders, a producer recovering from
its own crash, or a message redriven from the dead-letter queue all republish the same order under a
**new** `eventId`, a new `occurredAtUtc` and usually a new `correlationId`. Nothing in the envelope
survives, but the order is the same order.

Specification v1 defined one `PayloadSha256` over the whole event, stored on both the idempotency
record and the order item. That value cannot answer both questions. On the second route the stored
hash and the computed hash differ for a valid republish, and the only classification available is
conflict — which returns the record as a batch item failure, retries it to `maxReceiveCount`, sends
it to the dead-letter queue and raises a high-severity alarm. A correct order would have been
rejected, loudly, on a routine operational action.

The stored entity shapes depend on the answer, so the table schema in Epic 2 cannot be written until
it is settled.

## Decision

### Two hashes over disjoint scopes

| Scope | Key | Hash | Stored on | Question it answers |
| --- | --- | --- | --- | --- |
| Event-level | `eventId` | `EnvelopeSha256` | Idempotency record | Have I seen this exact event before? |
| Domain-level | `orderId` | `BusinessSha256` | Order item | Does this order already exist with the same business data? |

`EnvelopeSha256` covers the whole canonical event, envelope and data together. `BusinessSha256`
covers the canonical `data` object alone. A republish therefore differs in the first and matches in
the second, which is exactly the distinction the two routes need. `PayloadSha256` is superseded.

`causationId` sits inside the envelope scope. Two events that differ only there are different
events, not duplicates.

### A canonical representation, never the wire body

The raw SQS body is not hashed. Two publishers can express one event with different whitespace,
property order or number spelling, and hashing bytes would make those look like different events.

Hashing the deserialized contract types directly is also rejected. Those types are shaped by what
publishers send and will change as the contract does, so a field added for the reader's convenience
would silently enter the hash. Canonicalisation maps the parsed event into explicit types under
`Idempotency/` whose only purpose is to be hashed, with property names and property order stated per
property rather than left to a serializer policy. Identifiers render as the 36-character hyphenated
lowercase form and instants in round-trip form with the offset written rather than folded into the
instant.

### One canonicaliser, nested, rather than two

`BusinessSha256` hashes the `CanonicalOrderData` instance that the canonical envelope already holds.
The two scopes therefore share one canonicalisation of the business payload by construction, not by
convention. A test asserts the business JSON appears verbatim inside the envelope JSON, so drift is
caught as a substring failure rather than as two hashes that quietly stopped agreeing.

### The event identifier is both the key and the token

`IdempotencyKey` is the `eventId` verbatim. The transaction's `ClientRequestToken` is the same
string.

DynamoDB caps `ClientRequestToken` at 36 characters and a hyphenated UUID fills exactly 36, so there
is no headroom. A prefix, an environment namespace or a table-name qualifier would read as harmless
and would fail every transaction. `IdempotencyClaim` holds the constraint and exposes the token as a
named property so a reader can see that passing the key was deliberate.

### Every persisted value is a function of the event

`ExpirationEpochSeconds` derives from `occurredAtUtc` plus the configured retention. `CreatedAtUtc`
is `occurredAtUtc`. Neither reads a clock, and `IdempotencyClaim` accepts no `TimeProvider`, so a
later change cannot reach for one without changing the signature.

The rule is not stylistic. DynamoDB raises `IdempotentParameterMismatchException` when a token is
reused inside its ten-minute window with a different request body, so a wall-clock value anywhere in
the request would make two attempts at the same event milliseconds apart build different bodies and
fail the second. Anchoring the expiry to `now` would also extend a record's life on every retry.

`TimeProvider` remains injected for latency metrics, the invocation deadline and skew validation.
None of those values is persisted.

### Determinism is proved by committed vectors, not by repetition

Hashing an event twice in one process proves nothing, because both values move together when the
serializer, the runtime or the canonical model changes. `tests/ReliableOrders.UnitTests/Vectors/`
holds fixed events with the hashes this repository has committed to producing for them. They cover a
root event, a caused event, unknown top-level fields, a republish sharing another vector's data, a
non-ASCII description that pins string escaping, and the largest representable amount.

## Consequences

Two hashes must be computed for every message and both must be stored. The cost is one extra SHA-256
over a payload of a few hundred bytes, which is not measurable against a DynamoDB round trip.

Classification reads the two hashes at different indexes of the cancelled transaction's reasons, and
the event-level check takes precedence. A repeat of the same `eventId` carrying a different envelope
is a conflict regardless of what the order item holds.

The order item's hash outlives the idempotency record. After the record's TTL passes, a replayed
event falls through to the order-level check and is still classified correctly, which is only true
because the order carries a business hash of its own.

Unknown top-level fields are dropped by canonicalisation and reach neither hash. Two events
differing only in fields this schema version does not know about are duplicates of one another, and
the first one's data is what gets stored. A v1 processor has no basis for treating unrecognised data
as business-significant, and hashing what arrived would turn every future additive field into a
source of conflicts against records written before it existed.

A change to canonicalisation is a schema migration. Every stored record was written against the
current bytes, so a change reclassifies replays as conflicts across the whole table. A failing
vector after an SDK or runtime upgrade is the intended signal and must never be answered by updating
the expected constant.

`ReliableOrders.Core` exposes the canonical types as `internal` with `InternalsVisibleTo` for the
unit tests, because a canonicalisation change has to be reviewable as a JSON diff rather than only
as a different hexadecimal string.

## Alternatives considered

| Alternative | Why it was rejected |
| --- | --- |
| One `PayloadSha256` over the whole event | Cannot distinguish a republish from divergence, so it dead-letters valid orders and alarms on routine replays. This is the v1 design being superseded. |
| One hash over `data` alone | Loses the event-level check. A redelivered event whose order already exists would be indistinguishable from a new publisher sending the same order, and envelope corruption under a reused `eventId` would go unnoticed. |
| Hash the raw SQS body | Formatting differences between publishers, or between a publisher and its own retry, produce different hashes for one event. |
| Two independent canonicalisers, one per scope | Nothing keeps them in step. A field added to one and not the other is invisible until stored records stop matching in production. |
| A composite or decorated idempotency key | Overflows the 36-character `ClientRequestToken` limit, which a bare UUID fills exactly. |
| Derive expiry and creation time from `TimeProvider` | Makes the request body differ between attempts at one event, which `IdempotentParameterMismatchException` turns into a failure, and extends record lifetime on every retry. |
| Prove determinism by hashing twice in one process | Both values move together under a serializer or runtime change, so the test passes precisely when it should fail. |
