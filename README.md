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

There is nothing to deploy yet. Once the first milestone lands this section will carry deployment, a
demonstration of duplicate suppression and dead-letter handling, and teardown.

Until then, [Architecture](docs/architecture.md) describes every component and its contract, and
[Infrastructure](docs/infrastructure.md) specifies every AWS resource the CDK stack creates.

## Contributing

The backlog is maintained as
[GitHub issues](https://github.com/SalZaki/aws-dotnet-lambda-sqs-idempotency/issues), organised into
epics and milestones. [Delivery](docs/delivery.md#backlog) explains how they are structured and why
the plan is not kept in the documentation.

Contribution, security reporting and code of conduct policies are not written yet. They are tracked
by the repository foundation epic.

## Licence

[MIT](LICENSE).
