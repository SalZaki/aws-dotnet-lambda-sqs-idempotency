# Cost Model

What this service costs to run, in the two forms the question is actually asked: what a million
events consume, and what a deployed stack costs while nothing is happening to it.

The document is written in two layers on purpose. **Consumption** — requests, write units, log bytes,
traces — falls out of the design and changes only when the design does. **Price** is a snapshot of
what AWS charged for those units in `us-east-1` when this was written, in a table nothing in this
repository checks and no reader should quote without opening the pricing pages beside it. Where the
two disagree, the consumption column is the one this project stands behind.

## What a million events consume

The assumptions are the deployed development configuration: batch size 10, one receive per batch, no
duplicates, no retries, one publisher sending one message per request, and an invocation that runs
for 250 ms. Only the last of those is a guess rather than a setting, and it moves one row.

| Line item | Per million events | Where the number comes from |
| --- | --- | --- |
| SQS requests | 1,210,000 | 1,000,000 sends, ~110,000 receives returning ten records each, 100,000 batched deletes |
| Lambda invocations | 100,000 | One per batch of ten |
| Lambda compute | 12,500 GB-seconds | 100,000 invocations × 250 ms × 512 MB, the memory in [Lambda Function](infrastructure.md#lambda-function) |
| DynamoDB writes | 4,000,000 write request units | Two items per event, each under 1 KB, at **two** units per item because the write is transactional |
| DynamoDB reads | 0 | Classification reads the old image out of the cancellation reason; no `GetItem` runs and no read action is granted |
| CloudWatch Logs ingestion | ~0.69 GB | Derived below |
| X-Ray traces recorded | 1,000,000 | One trace per record, six spans inside it, and no sampler configured |
| Custom metrics | 10 metric-months | Ten metric names under one `Service`/`Environment` pair — a monthly charge, not a per-event one |

The transactional write is the line worth pausing on. `TransactWriteItems` consumes twice the
capacity of the equivalent unconditional `PutItem`, and the design writes two items rather than one,
so an order costs four write units where a naive implementation would spend one. That is the price of
[the failure window it closes](adr/0002-use-dynamodb-transactions.md), and it is not recoverable
without giving the window back.

A duplicate costs the same four units as a new order. DynamoDB bills a conditional write that fails
its condition, and a cancelled transaction is billed like the writes it attempted — so replay
suppression is correct, cheap in wall-clock terms, and not free.

### Where the log bytes come from

| Source | Bytes | Per million events |
| --- | --- | --- |
| Lambda's own `START`, `END` and `REPORT` lines | ~340 per invocation, estimated | 34 MB |
| `BatchStarted` and `BatchCompleted` | 651 per invocation | 65 MB |
| One terminal event per record | 537 per record | 537 MB |
| One EMF record per invocation | 540 per invocation | 54 MB |
| | | **~690 MB** |

Every figure but the first is measured rather than estimated: one invocation of ten records driven
through the real publisher and the real formatter into a `StringWriter`, which is what
`EmbeddedMetricsCapture` and `JsonLogCapture` already do for the unit tests that assert on emitted
bytes. The terminal line is `OrderCreated` carrying the full record scope with UUID identifiers,
which is the largest of the ordinary outcomes. Lambda's own platform lines are the estimate, because
the runtime writes them and nothing local does.

The EMF record is 540 bytes for a batch of ten, not the 2,048 that
`EmbeddedMetricsPublisher.InitialRecordBytes` reserves — that constant sizes a buffer so the common
case writes without growing, and reading it as a record size overstates this row fourfold.

## What that costs

Prices are `us-east-1` list prices, before any free tier, and are a snapshot rather than a source of
truth. A Region other than `us-east-1` prices most of these lines a few percent higher.

| Line item | Unit price | Per million events | Share |
| --- | --- | --- | --- |
| X-Ray traces recorded | $5.00 per million | $5.00 | 58% |
| DynamoDB writes | $0.625 per million write request units | $2.50 | 29% |
| SQS requests | $0.40 per million | $0.48 | 6% |
| CloudWatch Logs ingestion | $0.50 per GB | $0.35 | 4% |
| Lambda compute | $0.0000166667 per GB-second | $0.21 | 2% |
| Lambda requests | $0.20 per million | $0.02 | <1% |
| | | **~$8.56** | |

The always-free tier absorbs part of that at demonstration volume, and it is worth being exact about
which part. Lambda's requests and compute, Logs ingestion at 0.69 GB against 5 GB, and ten custom
metrics against ten are all inside their allowances and cost nothing. Three lines are not. DynamoDB
on-demand throughput has no free allowance at all, so its $2.50 is invoiced in full. X-Ray's first
100,000 traces are free and the remaining 900,000 are not, at $4.50. And SQS consumes 1,210,000
requests against an allowance of 1,000,000, so 210,000 of them are billed — $0.08, which is small
but is not the zero that "inside the free tier" would imply. With the alarms below, a million-event
month invoices around $7.80. The free tier is a property of the account rather than of this design,
which is why it is not in the table above.

## The dominant line item

**X-Ray, at 58% of the variable cost, and it is the only line here that is not paying for something
the design needs.**

Every other line is the mechanism. The four write units per order are the transaction; the SQS
requests are the delivery; the log bytes are the outcome record an incident is reconstructed from.
The trace is diagnostic — [Observability](observability.md#tracing-specification) says so in as many
words — and it is billed per trace recorded, once per record, because nothing configures a sampler
and the OpenTelemetry default records everything. At a 5% ratio that line falls to $0.25 and the
total falls by more than half, at the cost of a one-in-twenty chance that the record an operator
wants to look at is the one that was kept.

That leaves DynamoDB writes as the floor, which is the right shape for this project: the largest
irreducible cost is the correctness mechanism, and it is irreducible for a reason
[ADR 0002](adr/0002-use-dynamodb-transactions.md) argues.

CloudWatch Logs ingestion is fourth, and the observability design expected it first. That
expectation does not survive being measured. Publishing one EMF record per invocation rather than
one per record saves 457 bytes a record — 511 bytes ten times over against 540 bytes once — which is
457 MB and $0.23 per million events. Real, worth taking, and not the difference between first place
and fourth: per-record EMF would have moved Logs to third, ahead of SQS and still under a seventh of
the X-Ray line. The [aggregation decision](observability.md#metrics-specification) stands on its own
terms, because CloudWatch derives the same statistics from either shape and one record an invocation
is the more legible of the two. The cost argument made for it was the part that was wrong.

## What a deployed stack costs while idle

A stack that is deployed and receiving nothing bills for what exists rather than for what runs.

| Line item | Monthly |
| --- | --- |
| Alarms | $1.70 — twelve alarm metrics at $0.10, plus one composite at $0.50 |
| Dashboard | $0.00 — the first three in an account are free, and this is one |
| Log storage | $0.03 per GB retained, against a 30-day retention |
| Point-in-time recovery | $0.20 per GB-month of table size, where an environment enables it |
| Custom metrics | $0.00 — a metric is billed for a month it receives data in, and an idle stack publishes none |
| Queues, tables, function, log group | $0.00 — none of them is billed for existing |

The alarm line is the one that surprises. `ObservabilityConstruct` creates nine metric alarms, not
the eight the [required list](observability.md#required-alarms) numbers: alarm 7 is a composite, and
a composite is a rule over other alarms rather than an alarm over a metric, so the two halves it
reads — queue depth and the sum of processed and duplicate records — are metric alarms of their own.
CloudWatch then bills a metric-math alarm once per metric in its expression, which makes
`TableThrottlingOrErrors` three and `NoProgressNothingProcessed` two. Twelve alarm metrics, ten of
them inside the always-free allowance, and the composite outside it.

So an idle development stack costs $1.70 a month at list price, or 70 cents in an account whose free
alarm allowance nothing else is spending, plus whatever the tables and the retained log come to. The
number a reader should take from that is not the dollars: it is that **the stack does not stop
costing when the demonstration does**, which is why the README's deploy instructions carry a
teardown warning ahead of them.

## What changes the answer

- **A sampler.** The single largest lever, described above. `OTEL_SDK_DISABLED=true` removes the line
  entirely for a run that does not need traces.
- **Retry amplification.** A permanently failing message is delivered `maxReceiveCount` times, so a
  poison message costs five invocations' worth of logs and five transactions rather than one. The
  metrics are gated to first receipt, but the logs and the writes are not — they are what the retry
  history is read from.
- **Replay.** Duplicates cost what new orders cost, on every line except the order item that is not
  written. A replay storm is a bill as much as it is a correctness test.
- **Log level.** These figures assume the deployed level. Debug logging multiplies the largest of the
  log rows without moving anything else.
- **Batch size.** Invocation-scoped bytes — the platform lines, the batch events, the EMF record —
  are divided by the batch size. A batch of ten spends about 1.5 KB of log per invocation; a batch of
  one spends the same 1.5 KB per record.
- **Logs Insights.** Querying is billed per GB scanned, separately from ingestion, and a broad query
  over a month of retention scans everything ingested in it.

## What is not modelled

Data transfer, which is zero while every component sits in one Region; KMS, because the tables use
AWS-managed keys that carry no key charge; the alarm topic, whose first thousand email notifications
a month are free and which is not going to send a thousand; support plans; and the cost of the CI
that builds this, which is GitHub's rather than AWS's. The ephemeral end-to-end stack is not
modelled either: it deploys and destroys inside one nightly run, and what it spends is a rounding
error against the quarter-hour it spends waiting for a message to dead-letter.
