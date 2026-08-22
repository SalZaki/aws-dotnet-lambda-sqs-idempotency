# ADR 0002 — Write the Order and Its Idempotency Record in One Transaction

## Status

Accepted. Implemented by Story 2.2 in `src/ReliableOrders.Aws/DynamoDb/`.

## Context

Processing one event produces two writes: the order itself, and the record that says this event has
been processed. Whether those writes are one operation or two is the whole of the correctness
argument, because a retry can arrive between them.

The obvious implementation marks the event as seen and then saves the order:

1. Create the idempotency record.
2. Save the order.
3. Return success.

If the invocation stops between steps 1 and 2 — a timeout, a throttle, a deployment, a hardware
failure — a retry finds the idempotency record, concludes the event has been handled, and skips it.
The order is never saved and nothing reports that. Data loss, from a code path that looks correct in
review and passes every test that does not kill the process at the right microsecond. See [Failure
Window in a Mark-Then-Write Design](../correctness-model.md#failure-window-in-a-mark-then-write-design).

Reversing the order does not fix it. Saving the order first and marking afterwards leaves a window
where a retry writes a second order, which is the failure the idempotency record exists to prevent.

## Decision

Both writes go in a single `TransactWriteItems` call. Either both succeed or neither does.

Two conditions carry the idempotency model, one per item.

- The idempotency record is written only when the event-level key does not already exist.
- The order is written only when the order identifier does not already exist.

Both `Put` operations set `ReturnValuesOnConditionCheckFailure = ALL_OLD`, so a cancelled transaction
returns the conflicting items in the same response. Classification reads `CancellationReasons` and
needs no follow-up read, which also closes the window in which a time-to-live sweep could remove the
record between the failure and the read that would have explained it.

The request body is a pure function of the event. `ClientRequestToken` is the event identifier, a
bare UUID that fills the 36-character limit exactly, and every stored value derives from the event
rather than from a clock. See [Transaction Requests Must Be
Deterministic](../correctness-model.md#transaction-requests-must-be-deterministic).

## Consequences

There is no window. A retry either finds both rows or neither, and both cases are answered by the
condition expressions rather than by inference.

The two writes must live in one table family and one account, since `TransactWriteItems` does not
span accounts. Both tables are in this stack, so the constraint costs nothing today and would be a
change to this decision rather than a configuration change if an order ever had to be written
somewhere else.

A transactional write consumes twice the capacity of an equivalent unconditional `PutItem`, so these
two items cost four write units rather than two — the figure to size a table from, and the one
[Metrics Specification](../observability.md#metrics-specification) records for the cost model. A
transaction is also refused outright if the same item appears twice in one request, which has cost
nothing here: a batch of ten records produces ten transactions, each touching two distinct items.

Determinism becomes load-bearing rather than tidy. Because `ClientRequestToken` is the event
identifier, two attempts at the same event that built different request bodies would be rejected with
`IdempotentParameterMismatchException` — a routine retry turned into a hard error. That is why no
stored value may come from the clock, and why committed vectors prove it rather than a test that
hashes twice in one process.

## Alternatives considered

| Alternative | Why it was rejected |
| --- | --- |
| Mark the event, then save the order | An invocation that stops between the two writes loses the order permanently, and the retry that would have saved it is the thing that skips it. |
| Save the order, then mark the event | Leaves the opposite window: a retry between the writes creates a second order, which is the outcome the record exists to prevent. |
| A conditional put on the order alone, with no idempotency record | Answers "does this order exist" but not "has this event been processed". A republished order under a new event identifier could not then be distinguished from a divergent one — see [ADR 0005](0005-separate-envelope-and-business-hashes.md). |
| A single item holding both the order and the event | One condition instead of two, and no way to answer the two questions separately. It also makes the order's key the event's key, so a republish would write a second order. |
| Optimistic concurrency with a version attribute | Solves lost updates between concurrent writers, which is not the problem. The problem is a process that stops between two writes, and a version number does not survive that either. |
| A saga or an outbox with compensation | Both are answers to writes that cannot be made atomic. These two can be, in one call, so the machinery would buy nothing and would itself need a failure window argument. |
