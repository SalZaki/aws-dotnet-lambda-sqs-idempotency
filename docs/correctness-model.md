# Correctness Model

## Delivery Semantics

The source queue is an SQS standard queue. Messages can be delivered more than once. Lambda can also
retry an individual record after a timeout, service error, throttling event, or partial-batch
failure.

The application must therefore treat duplicates as a normal operating condition rather than as an
exceptional condition.

Use the following terminology consistently.

- **At-least-once delivery** means the infrastructure may deliver a record more than once.
- **Idempotent processing** means repeating the same operation does not create additional business
  effects.
- **Effectively-once order creation** means repeated delivery of the same logical order produces one
  stored order.
- **Exactly-once** is a phrase to avoid for the complete SQS-to-Lambda delivery path.

## Failure Window in a Mark-Then-Write Design

The following implementation is unsafe.

1. Create the idempotency record.
2. Save the order.
3. Return success.

If the Lambda invocation stops between steps 1 and 2, a retry sees the idempotency record and skips
the message even though the order was never saved. This creates data loss.

## Transactional Design

The worker uses `TransactWriteItems` to perform both writes atomically.

1. Put the idempotency record only when the event-level idempotency key does not already exist.
2. Put the order only when the order ID does not already exist.

Either both writes succeed or neither write succeeds.

Both `Put` operations must set `ReturnValuesOnConditionCheckFailure = ALL_OLD` so that a cancelled
transaction returns the conflicting items directly. See the [Duplicate and Conflict
Classification](#duplicate-and-conflict-classification) section.

## Two Idempotency Scopes Require Two Hashes

The design protects two distinct scopes, and a single hash cannot serve both.

| Scope | Key | Hash | Stored on | Question it answers |
| --- | --- | --- | --- | --- |
| Event-level | `eventId` | `EnvelopeSha256` | Idempotency record | Have I seen this exact event before? |
| Domain-level | `orderId` | `BusinessSha256` | Order item | Does this order already exist with the same business data? |

**`EnvelopeSha256`** covers the full canonical event, comprising `schemaVersion`, `eventId`,
`eventType`, `occurredAtUtc`, `source`, `correlationId`, `causationId`, and the complete `data`
object.

**`BusinessSha256`** covers the canonical `data` object alone, comprising `orderId`, `customerId`,
`currency`, `amountMinor`, `itemDescription`.

The distinction is load-bearing. A legitimate republish of the same logical order carries a **new**
`eventId` and a new `occurredAtUtc`, so its `EnvelopeSha256` necessarily differs from the first
event's. Classifying on the envelope hash alone would mark every such republish as a conflict and
route a valid order to the DLQ with a high-severity alarm. Only `BusinessSha256` can distinguish a
benign republish from genuine data divergence on the same order ID.

Specification v1 defined a single `PayloadSha256` stored on both items. That is superseded.

## Duplicate and Conflict Classification

Classification is driven entirely by `TransactionCanceledException.CancellationReasons`, which is
positionally aligned with the request's `TransactItems`. Index 0 is the idempotency put; index 1 is
the order put.

| Reason at index 0 | Reason at index 1 | Comparison | Result |
| --- | --- | --- | --- |
| `ConditionalCheckFailed` | any | `EnvelopeSha256` of returned item matches computed | `Duplicate` (success) |
| `ConditionalCheckFailed` | any | `EnvelopeSha256` differs | `Conflict` (permanent) |
| `None` | `ConditionalCheckFailed` | `BusinessSha256` of returned order matches computed | `Duplicate` (success) |
| `None` | `ConditionalCheckFailed` | `BusinessSha256` differs | `Conflict` (permanent) |
| `TransactionConflict`, `ThrottlingError`, or `ProvisionedThroughputExceeded` at either index | — | — | `TransientFailure` |
| `ValidationError` or `ItemCollectionSizeLimitExceeded` at either index | — | — | `PermanentFailure` (implementation defect; alarm) |

### Rules

- The event-level check takes precedence. When both indexes report `ConditionalCheckFailed`,
  evaluate the envelope hash first — a repeat of the same `eventId` carrying a different envelope is
  a conflict regardless of what the order item contains.
- A `Conflict` emits a high-severity log and the `IdempotencyConflicts` metric, and is treated as a
  permanent processing failure.
- **If a reason reports `ConditionalCheckFailed` but `Item` is null**, the conflicting record was
  removed between the condition evaluation and the response — most plausibly TTL expiry — or the SDK
  did not surface it. Classify as `TransientFailure` and retry. Never infer `Duplicate` or
  `Conflict` from an absent item.

This design does not perform a follow-up `GetItem` after cancellation. Specification v1 did, which
cost an extra round-trip on the most common retry path and opened a time-of-check/time-of-use window
between the cancelled transaction and the read.

## Transaction Requests Must Be Deterministic

The transaction sets a deterministic `ClientRequestToken` as an additional short-lived safeguard
against an indeterminate client response. This imposes two hard constraints.

**Token value.** `ClientRequestToken` is limited to 36 characters. A bare UUID `eventId` is exactly
36 characters and fits with no headroom. Do not prefix, namespace, or otherwise decorate the token —
use the `eventId` verbatim.

**Request body determinism.** DynamoDB raises `IdempotentParameterMismatchException` when the same
token is reused within the 10-minute idempotency window with a *different* request body. Therefore
every attribute value written by the transaction must be a pure function of the validated event and
static configuration. Specifically, the following hold.

- `ExpirationEpochSeconds` is derived from `occurredAtUtc + IdempotencyRetentionDays`, **not** from
  wall-clock `now`.
- `CreatedAtUtc` on the order item is `occurredAtUtc`, **not** wall-clock `now`.
- No attribute may carry a generated timestamp, GUID, or attempt counter.

If a wall-clock value leaked into the request body, two attempts at the same event milliseconds
apart would produce different bodies and the second would fail with
`IdempotentParameterMismatchException` — turning a routine retry of a valid event into an error.

`TimeProvider` is still injected, but is used only for latency metrics and the invocation deadline
check (see [OrderMessageProcessor](architecture.md#ordermessageprocessor)). It must not influence
any persisted attribute.

Because the request body is deterministic, `IdempotentParameterMismatchException` can only mean that
the same `eventId` carried different business data inside the token window. Map it to `Conflict`
(permanent), not to the default transient bucket.

Durable correctness comes from the conditional writes and persisted records. The token is a
10-minute convenience, not the correctness boundary.

## TTL Is Cleanup, Not a Correctness Boundary

DynamoDB TTL removes old idempotency records asynchronously. Code must not assume that an item
disappears at the exact expiry timestamp.

The order item remains protected by a conditional `OrderId` write even after the event-level
idempotency record has expired. After expiry, a replayed event falls through to the order-level
check and is classified on `BusinessSha256` per the [Duplicate and Conflict
Classification](#duplicate-and-conflict-classification)
section, which is
only correct because the order item carries its own business hash.

### Recommended initial retention

| Item | Retention |
| --- | --- |
| Source queue | 4 days |
| DLQ | 14 days |
| Idempotency record | 30 days |

The idempotency duration must be configurable rather than hard-coded.

## External Side Effects

The first release only performs DynamoDB writes that can participate in one transaction.

Do not later add payment, email, webhook, or other external calls directly after claiming an
idempotency key and assume the design remains correct. External side effects require a different
reliability pattern.

The recommended extension has four steps.

1. Atomically write the order and an outbox item.
2. Publish the outbox item through DynamoDB Streams.
3. Process the downstream side effect idempotently.
4. Record downstream delivery state.

This becomes the advanced transactional-outbox milestone.

## Error Classification

| Failure | Classification | Batch result | Expected final destination |
| --- | --- | --- | --- |
| Valid new event | Success | Do not return ID | Orders table |
| Duplicate event ID, matching envelope hash | Success | Do not return ID | Existing order remains |
| Duplicate order ID, matching business hash | Success | Do not return ID | Existing order remains |
| Malformed JSON | Permanent | Return ID in V1 | DLQ after retry policy |
| Unsupported schema version | Permanent | Return ID in V1 | DLQ after retry policy |
| Validation failure | Permanent | Return ID in V1 | DLQ after retry policy |
| Same event ID, different envelope hash | Permanent conflict | Return ID | DLQ and alarm |
| Same order ID, different business hash | Permanent conflict | Return ID | DLQ and alarm |
| `IdempotentParameterMismatchException` | Permanent conflict | Return ID | DLQ and alarm |
| `ConditionalCheckFailed` with null returned item | Transient | Return ID | Retry |
| DynamoDB throttling, `TransactionConflict`, or transient service fault | Transient | Return ID | Retry, then DLQ if unresolved |
| DynamoDB `ValidationError` in a cancellation reason | Permanent | Return ID | DLQ and alarm — indicates a code defect |
| Lambda nearing timeout | Deadline deferred | Return unprocessed ID | Retry |
| Unexpected exception | Transient by default | Return ID | Retry, then DLQ |

### Retry Amplification of Permanent Failures

Every permanent failure is returned in `BatchItemFailures` and redelivered until `maxReceiveCount`
is exhausted. With `maxReceiveCount = 5`, a single poison message produces **five**
`ValidationFailures` data points, and a single genuine conflict produces **five**
`IdempotencyConflicts` data points — against alarm thresholds of "greater than zero".

For V1, emit permanent-failure metrics only when `ApproximateReceiveCount == 1`, so each distinct
bad message contributes exactly one data point. Logs are still emitted on every attempt, with
`ApproximateReceiveCount` in the scope, so the retry history remains visible without distorting the
metric.

A later version can add an `OrdersQuarantineQueue` for permanent validation failures so invalid
events do not consume all retry attempts. If quarantine publishing fails, the source record must
still be returned as failed.
