# Sample Events

Fixtures for the `OrderCreatedV1` contract in [event-contract.md](../docs/event-contract.md). The
unit tests, the publisher and the end-to-end scenarios all read them, so changing one changes what
several suites assert.

| File | Shape | Expected outcome |
| --- | --- | --- |
| `valid-order-created-v1.json` | The reference event | Processed |
| `duplicate-order-created-v1.json` | Byte-identical redelivery of the valid event | Duplicate, event scope |
| `republished-order-created-v1.json` | Same order and business data, new event ID and time | Duplicate, order scope |
| `conflicting-order-created-v1.json` | Same order ID, different `amountMinor` | Conflict, order scope |
| `invalid-order-created-v1.json` | Well-formed JSON that breaks the contract rules | Permanent failure |

**Duplicate** repeats the `eventId` of the valid event, which the event-level idempotency key
catches. It is byte-identical rather than merely equivalent, because an at-least-once redelivery is
the same bytes twice.

**Republished** describes the same order under a new `eventId` and a later `occurredAtUtc`, so its
envelope hash differs from the original's while its business hash matches. Classifying on the
envelope hash alone would route it to the dead-letter queue with a high-severity alarm, which the
two-hash model in [correctness-model.md](../docs/correctness-model.md) prevents. Its `causationId`
points at the event it republishes.

**Conflicting** differs from the republished event in `amountMinor` and nothing else. That single
field is what separates a benign republish from genuine data divergence on the same order ID, so the
two files are otherwise kept identical.

**Invalid** parses and then fails validation. It is not a malformed body, and that distinction is
what it covers. It breaks five rules at once so the validator has to report more than one structured
failure: a non-UTC offset, an empty `orderId`, a lowercase `currency`, a negative `amountMinor` and
an empty `correlationId`.

Malformed bodies have no fixture. The tests construct them inline, since a file would imply they are
well-formed enough to keep.
