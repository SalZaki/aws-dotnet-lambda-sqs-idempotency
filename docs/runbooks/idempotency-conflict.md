# Idempotency Conflict

Raised by the `reliable-orders-<environment>-IdempotencyConflicts` alarm, which fires on a single
occurrence. A conflict means two different payloads claim the same identity. No retry resolves that,
so the record is a permanent failure and one of the two publishes is wrong.

This is the runbook where the answer is nearly always upstream. The service is behaving correctly
when it raises a conflict, and the useful output of this runbook is a statement about the publisher
rather than a repair here.

## Inspect

Find the conflicting records. Every conflict log line carries the scope, the reason and the hash
that was computed from the arriving event.

```text
fields @timestamp, Scope, Reason, ComputedHash, EventId, OrderId, CorrelationId, SqsMessageId
| filter Outcome = "PermanentFailure" and Reason like /^conflict\./
| sort @timestamp desc
```

`ComputedHash` is the hash of the message that was refused. The stored hash it disagreed with is on
the item already in DynamoDB, and the two together are what identify which publish is the odd one.

## Diagnose

Which hash diverged is the whole diagnosis. The two hashes cover disjoint scopes, decided in
[ADR 0005](../adr/0005-separate-envelope-and-business-hashes.md), and each answers a different
question about the publisher.

| `Reason` | `Scope` | Hash that diverged | What it means about the publisher |
| --- | --- | --- | --- |
| `conflict.envelope-hash-mismatch` | `Event` | `EnvelopeSha256`, over the whole canonical event | One `eventId` has been used for two different events. The publisher is reusing event identifiers instead of minting one per event. |
| `conflict.business-hash-mismatch` | `Order` | `BusinessSha256`, over the canonical `data` alone | Two publishes disagree about the contents of one order. The order was amended without a new `orderId`, or two producers own the same order. |
| `conflict.token-mismatch` | `TokenMismatch` | Neither, directly | DynamoDB refused a reused `ClientRequestToken` carrying a different request body. Because the request body is a pure function of the event, this is an envelope conflict inside the ten-minute token window. |

### An envelope conflict is an identifier defect

`EnvelopeSha256` covers the envelope and the data together, so it differs whenever anything about
the event differs, including `occurredAtUtc` and `causationId`. Two events that differ only in
`causationId` are different events and must not share an `eventId`.

The common cause is a publisher deriving `eventId` from something that is not unique per event, such
as the order identifier or a request identifier that is reused across retries at the publisher's own
layer. Ask the publisher what `eventId` is derived from. If the answer names anything other than a
value minted per event, that is the defect.

A republish is not this. A republished order carries a **new** `eventId`, so it does not collide at
event scope at all. It is recognised as a duplicate at order scope and acknowledged.

### A business conflict is a data disagreement

`BusinessSha256` covers the canonical `data` object alone, so it is stable across republishes and
changes only when the order's contents change. A conflict here means the same `orderId` has been
published twice with different business data.

Two causes are worth separating, and the stored item tells them apart.

```bash
STACK=ReliableOrders-dev
ORDERS=$(aws cloudformation describe-stacks --stack-name "$STACK" \
  --query "Stacks[0].Outputs[?OutputKey=='OrdersTableName'].OutputValue" --output text)

aws dynamodb get-item --table-name "$ORDERS" \
  --key '{"OrderId":{"S":"<order id>"}}' --consistent-read
```

Compare the stored business data against the refused message.

- The stored order is the earlier one and the refused message is an amendment. The publisher is
  treating this pipeline as an update channel, which it is not. Orders are written once. An
  amendment needs its own event type and its own decision, not a second publish under the same
  identifier.
- The stored order and the refused message describe genuinely different orders. Two producers have
  allocated the same `orderId`. That is an identifier-allocation defect and is the more serious of
  the two, because it means some other pair of orders may already have collided silently.

### A token mismatch is an envelope conflict seen from DynamoDB

Read it as `conflict.envelope-hash-mismatch` for the purpose of talking to the publisher. It is
classified separately only because the refusal came from the transaction's `ClientRequestToken`
rather than from a condition on the item, and it can only occur inside the ten-minute window in
which DynamoDB remembers the token.

## Repair

There is nothing to repair in this service. Every conflict is a correct refusal, so a change here
would be a change to what the service considers the same order, which belongs in the
[Correctness Model](../correctness-model.md) rather than in an incident.

| Diagnosis | Action |
| --- | --- |
| Reused `eventId` | The publisher mints one `eventId` per event and republishes. The order is written under the new event. |
| Amendment published under an existing `orderId` | The amendment is not a create. Reject it with the publisher and decide the amendment path outside this incident. |
| Two producers allocating one `orderId` | Escalate. Audit the identifier allocation on both sides before republishing anything. |

Never delete the stored order to let a conflicting message through. The stored order is the one that
was accepted first and the conflict is the evidence of the disagreement. Deleting it destroys the
evidence and writes the payload that is more likely to be wrong.

## Redrive

Do not redrive a conflicting message. It failed permanently, it will fail identically, and the
redrive costs another full retry cycle. The conflicting message is superseded by whatever the
publisher sends after the defect is fixed.

If a conflicting message has already reached the dead-letter queue, leave it there until the
publisher-side diagnosis is settled, then discard it rather than moving it.

## Verify

The incident is closed when all of the following hold.

- The `reliable-orders-<environment>-IdempotencyConflicts` alarm has returned to OK and no new
  conflict has been logged since the publisher's fix was deployed.
- The affected orders exist once each in the orders table, with the business data the publisher
  intends.
- The publisher-side defect is recorded, because a conflict raised twice by the same cause is a
  process failure rather than a new incident.
