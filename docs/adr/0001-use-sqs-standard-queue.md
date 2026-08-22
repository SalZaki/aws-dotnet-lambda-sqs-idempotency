# ADR 0001 — Use an SQS Standard Queue, and Make Ordering a Non-Goal

## Status

Accepted. Implemented by Story 4.1 in `infra/ReliableOrders.Cdk/Constructs/MessagingConstruct.cs`.

## Context

The worker consumes order events published by another service. SQS offers two queue types, and the
choice decides what the application is obliged to handle rather than merely how it is configured.

A standard queue delivers at least once and does not preserve order. A FIFO queue preserves order
within a message group and offers content-based deduplication over a five-minute window.

The second of those reads like idempotency, and that resemblance is the reason this record exists.
A FIFO queue's deduplication is a delivery-layer convenience with a five-minute memory. The
duplicates this project exists to survive are not bounded by five minutes: a publisher retrying an
hour later, an operator redriving a dead-letter queue the next morning, and a republished order under
a new event identifier are all routine, and none of them is a duplicate any queue can recognise. See
[Delivery Semantics](../correctness-model.md#delivery-semantics).

Ordering is a separate question. Orders are independent of one another: nothing in the domain says
that order B must be created after order A, and the two hashes decide what happens when the same
order arrives twice. What ordering would buy is protection against a sequence this system does not
have.

## Decision

The source queue is an SQS standard queue, and ordering between orders is an explicit non-goal, named
as such in [Non-Goals](../overview.md#non-goals).

At-least-once delivery is treated as a normal operating condition rather than an exceptional one.
Every duplicate is answered by the idempotency model — a transactional write conditioned on the
event-level key and the order key — rather than by anything the queue does. The retention on the
idempotency table is thirty days, which is the window that actually matters and is four orders of
magnitude longer than the one a FIFO queue offers.

The queue is named explicitly rather than generated, because the dead-letter queue's redrive allow
policy has to name it and a resource reference in both directions is a circular dependency
CloudFormation refuses.

## Consequences

Duplicate handling is the application's job and cannot be delegated. That is a cost, and it is the
cost this project was written to pay: the mechanism is visible, tested, and observable, where a FIFO
queue's would be none of those and would still not cover the cases above.

Throughput is not capped by a FIFO queue's transaction limit, and no publisher has to choose a message
group identifier — a choice that would have become a scaling decision disguised as a correctness one.

Nothing in the system may assume that order A was stored before order B. Any future requirement for
ordering between orders is a change to this decision rather than a configuration change, and the
[Non-Goals](../overview.md#non-goals) section is where it is recorded as absent.

Because the queue names are physical, one account and Region holds one deployment of each
environment. A second developer wanting their own stack needs a new environment name rather than a
second copy of `dev`, which is also what makes the ephemeral end-to-end stacks possible — see
[Ephemeral AWS End-to-End Test](../ci-cd.md#ephemeral-aws-end-to-end-test).

## Alternatives considered

| Alternative | Why it was rejected |
| --- | --- |
| A FIFO queue with content-based deduplication | The deduplication window is five minutes. A publisher retry an hour later, an operator redriving the dead-letter queue, and a republish under a new event identifier are all duplicates outside it, so the application would need the idempotency model anyway — and would then have two mechanisms, one of which silently covers a fraction of the cases. |
| A FIFO queue for ordering, deduplication ignored | Buys ordering the domain does not need, at 300 transactions per second per queue — 3,000 messages with batching, or more under high-throughput mode with its own per-group limits — and makes every publisher choose a group identifier. The gain is protection against a sequence that does not exist. |
| A standard queue with a deduplication cache in front | A second store to make consistent with the one the transaction already writes, and a cache miss is indistinguishable from a new event. The idempotency table is that cache, with a condition expression instead of a lookup. |
| Kinesis or a log-structured stream | Ordering per shard and replay by position, at the cost of shard management and a consumer model this worker does not need. The reliability question here is duplicate suppression, not replay. |
