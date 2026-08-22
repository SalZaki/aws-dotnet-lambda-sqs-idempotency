# Reliable Serverless .NET 10 SQS Worker with Transactional Idempotency

An event-driven order processor in .NET 10. An Amazon SQS standard queue invokes an AWS Lambda
function with batches of order events. The worker validates each event, prevents duplicate business
effects, stores the order atomically in DynamoDB, reports per-record failures, and lets repeatedly
failing messages move to a dead-letter queue.

[![markdownlint](https://github.com/SalZaki/aws-dotnet-lambda-sqs-idempotency/actions/workflows/markdownlint.yml/badge.svg)](https://github.com/SalZaki/aws-dotnet-lambda-sqs-idempotency/actions/workflows/markdownlint.yml)

## Status

**Design complete. Implementation started.** The specification is finished and reviewed, and the
backlog exists as GitHub issues. The event contract and its parser are in place. Nothing is
deployable yet: there is no persistence, no Lambda handler and no infrastructure.

Progress is tracked through the
[milestones](https://github.com/SalZaki/aws-dotnet-lambda-sqs-idempotency/milestones), which run
from correctness first through to advanced reliability patterns.

## What it claims, and what it does not

SQS and Lambda provide **at-least-once delivery**. Duplicate delivery is a normal operating
condition here, not an exception, and it is tested as one.

The application provides **idempotent, effectively-once business effects** for order creation. It
does not claim end-to-end exactly-once delivery, and the documentation avoids that phrase
deliberately.

The central mechanism is a single DynamoDB transaction that writes the order and its idempotency
record as one all-or-nothing operation. That removes the failure window created by marking a message
processed before the order has actually been saved. The
[Correctness Model](docs/correctness-model.md) explains why that window exists and what it costs.

## Documentation

Start with the [documentation index](docs/README.md). The load-bearing documents are these.

| Document | Covers |
| --- | --- |
| [Overview](docs/overview.md) | What the project is, its goals and its explicit non-goals |
| [Correctness Model](docs/correctness-model.md) | Delivery semantics, the two hash scopes, transaction determinism, failure classification |
| [Event Contract](docs/event-contract.md) | The versioned envelope, validation rules, canonical hashing |
| [Architecture](docs/architecture.md) | C4 context and container diagrams, components, repository layout |
| [Testing Strategy](docs/testing-strategy.md) | Five test levels, and which emulator is trustworthy for what |

### Architecture decisions

These decisions carry the rest of the design, and each is recorded where it can be argued with rather
than re-argued. Every record states its context, the decision, what it costs, and the alternatives it
beat.

| Record | Decides |
| --- | --- |
| [0001](docs/adr/0001-use-sqs-standard-queue.md) | A standard queue over FIFO, and ordering between orders as an explicit non-goal |
| [0002](docs/adr/0002-use-dynamodb-transactions.md) | One transaction for the order and its idempotency record, and the failure window that closes |
| [0003](docs/adr/0003-use-dotnet-10-managed-runtime.md) | The managed .NET 10 runtime over a container image, with Native AOT deferred to a benchmark |
| [0004](docs/adr/0004-use-opentelemetry.md) | OpenTelemetry as the one tracing pipeline, with X-Ray active tracing disabled |
| [0005](docs/adr/0005-separate-envelope-and-business-hashes.md) | Two hashes over disjoint scopes, so a republish is a duplicate and not a conflict |
| [0006](docs/adr/0006-set-invariant-globalization.md) | Invariant globalization repository-wide, so an event hashes the same everywhere |

## What this project exercises

A compact worker, but it covers a broad set of commercially relevant ground.

**AWS.** Lambda event source mappings. SQS batching, visibility timeouts, retries, dead-letter
queues and redrive. DynamoDB conditional writes, transactions, TTL, on-demand capacity and
point-in-time recovery. CloudWatch Logs, Embedded Metric Format, dashboards and alarms.
OpenTelemetry and the AWS Distro for OpenTelemetry. IAM least privilege. AWS CDK v2 in C#. Secure
GitHub-to-AWS deployment with OpenID Connect.

**.NET.** .NET 10 and modern C#. Dependency injection and composition roots. `System.Text.Json`
source generation. Immutable message contracts. Validation and explicit error classification.
Cancellation and timeout handling. AWS SDK for .NET v4. Unit, integration, architecture and
end-to-end testing. Central package management and reproducible builds.

**Distributed systems.** At-least-once delivery. Idempotency and duplicate detection. Atomicity and
failure windows. Poison-message handling. Partial batch responses. Backpressure and concurrency
control. Payload versioning. Correlation and causation. Transactional outbox as a future extension.

## Getting started

The .NET SDK pinned in [`global.json`](global.json) is enough to build and to run the tests that
matter most.

```bash
dotnet restore ReliableOrders.slnx
dotnet build ReliableOrders.slnx -c Release
dotnet test ReliableOrders.slnx -c Release --filter "Category!=Integration"
```

Two more are needed for the whole suite. **Node.js**, because every CDK test synthesises through
jsii, which runs `node` as a child process — without it those tests fail on a missing executable
rather than on anything they assert. **Docker**, for the container-backed integration tests, which
carry the `Integration` category and are excluded by the filter above.

```bash
dotnet test ReliableOrders.slnx -c Release
```

The SQS tests need one thing further: LocalStack requires an auth token, free for non-commercial
use, in `LOCALSTACK_AUTH_TOKEN`. Without one they skip with a reason rather than fail, so the command
above is safe to run on a machine that has never been set up for them. Behind a TLS-inspecting
corporate proxy they need `LOCALSTACK_CA_BUNDLE` as well. Both are explained in [SQS
Emulation](docs/testing-strategy.md#sqs-emulation), along with why DynamoDB deliberately uses a
different emulator.

Formatting is verified by the build rather than by a separate step, so a layout violation is a build
error. Fix one with `dotnet format ReliableOrders.slnx`.

Package versions live in [`Directory.Packages.props`](Directory.Packages.props) and the resolved
graph is committed as a `packages.lock.json` per project. After changing a package, restore and
commit the regenerated lock files.

There is nothing to deploy to AWS yet. Until there is, the flows can be run locally — see below —
and [Architecture](docs/architecture.md) describes every component and its contract while
[Infrastructure](docs/infrastructure.md) specifies every AWS resource the CDK stack creates.

## Running it locally

`compose.yaml` runs the whole path on one machine: a queue, two tables, and the function itself. The
tests need none of it — `dotnet test` starts and disposes what it needs from code — so this is for
watching the flows rather than asserting them.

**What is real, and what is not.** The function runs on `public.ecr.aws/lambda/dotnet:10`, the base
image AWS publishes for the runtime this project deploys to, through the runtime interface emulator
that image carries. The handler, the serializer, the invocation context and the DynamoDB transaction
are the deployed ones. DynamoDB is the official `amazon/dynamodb-local`, because the whole
duplicate-versus-conflict path reads `CancellationReasons` and LocalStack is not dependable there.
SQS is LocalStack, which is trusted for the narrow set of behaviours
[SQS Emulation](docs/testing-strategy.md#sqs-emulation) lists.

The event source mapping is a stand-in, and it is the only one. It batches the way the deployed
mapping batches; concurrency, scaling and IAM are not modelled here, nothing is measured against real
latency, and no trace is exported.
Story [#32](https://github.com/SalZaki/aws-dotnet-lambda-sqs-idempotency/issues/32) is where a real
account answers those, and nothing below substitutes for it.
[The Local Development Stack](docs/testing-strategy.md#the-local-development-stack) is the full
account of what differs and why.

### Starting it

Docker, and a LocalStack auth token in `LOCALSTACK_AUTH_TOKEN` — free for non-commercial use, and
required since the community and pro images merged. Nothing else: the images are built from source,
so no local publish has to be current.

```bash
export LOCALSTACK_AUTH_TOKEN=...
docker compose up --build
```

Behind a TLS-inspecting corporate proxy, licence activation fails with exit code 55 and a message
about a licensing server it cannot reach. Add the overlay and point it at the interceptor's root
certificate — [SQS Emulation](docs/testing-strategy.md#sqs-emulation) has the detail, including the
trap that makes the obvious fix look like it did nothing.

```bash
export LOCALSTACK_CA_BUNDLE=/path/to/interceptor-root.crt
docker compose -f compose.yaml -f local/compose.ca-bundle.yaml up --build
```

The stack is ready when the mapping reports the queue it is polling. In a second terminal, take the
two queue URLs — every command below uses them, and `cli` is the AWS CLI already pointed at both
emulators, so nothing has to be installed to follow along.

```bash
QUEUE=$(docker compose run --rm -T cli sqs get-queue-url \
  --queue-name reliable-orders-local --query QueueUrl --output text)
DLQ=$(docker compose run --rm -T cli sqs get-queue-url \
  --queue-name reliable-orders-local-dlq --query QueueUrl --output text)
```

### The flows

Each one publishes an event from [`samples/`](samples/README.md) and is read in the `docker compose`
output. `samples/README.md` says what each file is and why.

**Valid.** One order is written, with its idempotency record, in one transaction.

```bash
docker compose run --rm -T cli sqs send-message --queue-url "$QUEUE" \
  --message-body file:///repo/samples/valid-order-created-v1.json
docker compose run --rm -T cli dynamodb scan --table-name reliable-orders-local-orders
```

**Duplicate.** The same event again, byte for byte, as an at-least-once redelivery is. Still one
order. Within ten minutes of the first the log says `Processed` a second time rather than
`Duplicate`, because the transaction's `ClientRequestToken` is the event identifier and DynamoDB
replays the original result inside that window; after it, the conditional writes classify it as
`Duplicate(Event)`. Both are correct, and the claim that holds either way is that no second order is
written.

```bash
docker compose run --rm -T cli sqs send-message --queue-url "$QUEUE" \
  --message-body file:///repo/samples/duplicate-order-created-v1.json
docker compose run --rm -T cli dynamodb scan --table-name reliable-orders-local-orders --select COUNT
```

**Republish.** The same order under a new event identifier and a later time, which is what an
upstream retry looks like. Its envelope hash differs and its business hash does not, so it is
`Duplicate(Order)` rather than a conflict — the distinction the
[Correctness Model](docs/correctness-model.md) exists for. Change `amountMinor` and it becomes one:
that is the conflicting sample, and it is the only field between them.

```bash
docker compose run --rm -T cli sqs send-message --queue-url "$QUEUE" \
  --message-body file:///repo/samples/republished-order-created-v1.json
docker compose run --rm -T cli sqs send-message --queue-url "$QUEUE" \
  --message-body file:///repo/samples/conflicting-order-created-v1.json
```

**Mixed batch.** One bad record in a batch must not cost the good ones their progress. Stopping the
mapping first is what makes the batch a batch: it is whatever is on the queue when the mapping next
polls.

```bash
docker compose stop mapping
docker compose run --rm -T cli sqs send-message --queue-url "$QUEUE" \
  --message-body file:///repo/samples/valid-order-created-v1.json
docker compose run --rm -T cli sqs send-message --queue-url "$QUEUE" \
  --message-body file:///repo/samples/invalid-order-created-v1.json
docker compose start mapping
```

The mapping reports `Batch of 2: 1 deleted, 1 returned for redelivery`. The valid record is gone
from the queue and the invalid one is not, which is the whole of what a partial batch response buys.

If it reports two batches of one instead, nothing is wrong: SQS is entitled to return fewer messages
than are available, and the mapping waits only the batching window the deployed one waits. Send them
again and they will usually arrive together.

**Poison message.** The invalid event has no path forward, so it is returned every time and reaches
the dead-letter queue on its sixth delivery — five receives, matching what the deployed queue allows.
Redelivery is immediate here rather than after a visibility timeout, so this takes seconds instead of
the quarter of an hour a real queue spends on it.

```bash
docker compose run --rm -T cli sqs receive-message --queue-url "$DLQ" \
  --max-number-of-messages 10 --visibility-timeout 0
```

### Stopping it

```bash
docker compose down -v
```

Both emulators are in memory, so this discards every order, every idempotency record and every queue.
That is the point: the next run starts from nothing, and a flow cannot appear to work because of a row
the last session left behind.

## Contributing

The backlog is maintained as
[GitHub issues](https://github.com/SalZaki/aws-dotnet-lambda-sqs-idempotency/issues), organised into
epics and milestones. [Delivery](docs/delivery.md#backlog) explains how they are structured and why
the plan is not kept in the documentation.

| Policy | Covers |
| --- | --- |
| [Contributing](CONTRIBUTING.md) | How to build, test, branch and commit, and what CI enforces before a change can merge |
| [Support](SUPPORT.md) | Where to ask, what the documents already answer, and what is unlikely to get a reply |
| [Security](SECURITY.md) | How to report a vulnerability privately, what is in scope, and what is already automated |
| [Code of Conduct](CODE_OF_CONDUCT.md) | The Contributor Covenant, and how to report a concern |

## Licence

[MIT](LICENSE).
