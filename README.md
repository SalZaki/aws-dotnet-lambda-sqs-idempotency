# Reliable Serverless .NET 10 SQS Worker with Transactional Idempotency

An event-driven order processor in .NET 10. An Amazon SQS standard queue invokes an AWS Lambda
function with batches of order events. The worker validates each event, prevents duplicate business
effects, stores the order atomically in DynamoDB, reports per-record failures, and lets repeatedly
failing messages move to a dead-letter queue.

[![markdownlint](https://github.com/SalZaki/aws-dotnet-lambda-sqs-idempotency/actions/workflows/markdownlint.yml/badge.svg)](https://github.com/SalZaki/aws-dotnet-lambda-sqs-idempotency/actions/workflows/markdownlint.yml)

## Status

**Deployable, and not yet released.** The worker, the infrastructure that runs it and the pipeline
that deploys it are all in place: the processing path is implemented and tested at five levels, the
CDK application deploys the whole stack, and a push to `main` deploys it through OpenID Connect with
no AWS credential stored anywhere. What is left before the first tagged release is the
demonstration assets — recordings of the flows this file describes in words.

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

Duplicate suppression works on two hashes rather than one, over deliberately disjoint scopes. The
**envelope hash** covers the whole event — identifier, timestamp and payload — and answers whether
this exact message has been seen before. The **business hash** covers the order alone — who, what,
how much — and answers whether that order already exists, however many times it was published. A
redelivery repeats the event identifier and agrees on the envelope hash, so it is ignored. A
republish carries a new event identifier and the same business hash, which makes it a duplicate
rather than a conflict. Two events claiming one order with different amounts match on the order
identifier and not on its hash, and that is the conflict worth alarming on.
[Two Idempotency Scopes Require Two
Hashes](docs/correctness-model.md#two-idempotency-scopes-require-two-hashes) has the classification
table in full.

## Documentation

Start with the [documentation index](docs/README.md). The load-bearing documents are these.

| Document | Covers |
| --- | --- |
| [Overview](docs/overview.md) | What the project is, its goals and its explicit non-goals |
| [Correctness Model](docs/correctness-model.md) | Delivery semantics, the two hash scopes, transaction determinism, failure classification |
| [Event Contract](docs/event-contract.md) | The versioned envelope, validation rules, canonical hashing |
| [Architecture](docs/architecture.md) | C4 context and container diagrams, components, repository layout |
| [Testing Strategy](docs/testing-strategy.md) | Five test levels, and which emulator is trustworthy for what |
| [Threat Model](docs/threat-model.md) | Trust boundaries, the six threats, and what each mitigation leaves behind |
| [Cost Model](docs/cost-model.md) | What a million events consume, which line item dominates, and what an idle stack costs |

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
dotnet test --solution ReliableOrders.slnx -c Release \
  -- --filter-not-trait "Category=Integration"
```

The end-to-end tests are not excluded there and do not need to be: with no deployed stack to point
them at, each one skips and says why. What is excluded is the container-backed suite, which would
otherwise fail on a machine with no Docker rather than on anything it asserts.

Two more are needed for the whole suite. **Node.js**, because every CDK test synthesises through
jsii, which runs `node` as a child process — without it those tests fail on a missing executable
rather than on anything they assert. **Docker**, for the container-backed integration tests, which
carry the `Integration` category and are excluded by the filter above.

```bash
dotnet test --solution ReliableOrders.slnx -c Release
```

The SQS tests need one thing further. LocalStack requires an auth token, free for non-commercial
use, in `LOCALSTACK_AUTH_TOKEN`. Without one they skip with a reason rather than fail, so the command
above is safe on a machine that has never been set up for them.

```bash
export LOCALSTACK_AUTH_TOKEN=...  # from https://app.localstack.cloud
dotnet test --solution ReliableOrders.slnx -c Release
```

Set it where the runner will read it, which is not always where it was typed. An export reaches
`dotnet test` in that shell and nothing else, while an IDE started from the desktop inherits the
desktop's environment — so Rider or Visual Studio on Windows reads the Windows user variables rather
than a WSL shell profile, and the same eight tests can skip in one and fail in the other from one
working tree. CI reads a repository secret of the same name.

The container-backed suite can be run on its own, and so can the subset of it that needs the token,
which is quicker than the whole solution when the emulators are what is being worked on.

```bash
dotnet test --project tests/ReliableOrders.IntegrationTests \
  -- --filter-trait "Category=Integration"
dotnet test --project tests/ReliableOrders.IntegrationTests \
  -- --filter-trait "Category=RequiresLocalStackToken"
```

A token that is present but rejected fails those eight rather than skipping them, and the container
exits 55 before opening its edge port. Behind a TLS-inspecting corporate proxy they need
`LOCALSTACK_CA_BUNDLE` as well, and the failure looks much the same. [SQS
Emulation](docs/testing-strategy.md#sqs-emulation) covers telling the two apart, both variables, and
why DynamoDB deliberately uses a different emulator.

Formatting is verified by the build rather than by a separate step, so a layout violation is a build
error. Fix one with `dotnet format ReliableOrders.slnx`.

Package versions live in [`Directory.Packages.props`](Directory.Packages.props) and the resolved
graph is committed as a `packages.lock.json` per project. After changing a package, restore and
commit the regenerated lock files.

The flows can be run [locally](#running-it-locally) on emulators or against [a deployed
stack](#deploying-it-to-aws), and the two are not authoritative for the same things.
[Architecture](docs/architecture.md) describes every component and its contract, and
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
mapping batches; concurrency, scaling and IAM are not modelled here, nothing is measured against
real latency, and no trace is exported. A real account answers those, which is what the
[end-to-end suite](tests/ReliableOrders.EndToEndTests/OrderProcessingTests.cs) is for, and nothing
below substitutes for it. [The Local Development
Stack](docs/testing-strategy.md#the-local-development-stack) is the full account of what differs and
why.

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

## Deploying it to AWS

The deployment is one CDK application holding two stacks. `ReliableOrders-dev` is the queues, the
tables, the function and the dashboard, sized by the environment it is named for.
`ReliableOrders-DeploymentIdentity` is the roles GitHub Actions assumes to deploy, which is a
separate stack for a reason [Infrastructure](docs/infrastructure.md#deployment-identity) argues.
Every resource either creates, and every value that sizes it, is specified in
[Infrastructure](docs/infrastructure.md#aws-resource-specification).

### What it costs, and what does not stop when the demonstration does

A deployed stack bills for what exists rather than only for what runs, so the last command under
this heading is the first one to read. [Tearing it down](#tearing-it-down) removes everything the
rest of it creates, and nothing removes it for you.

| Category | A development stack |
| --- | --- |
| Per million events | ~$8.56 at `us-east-1` list price |
| — of which traces | 58%, because every trace is recorded and no sampler is configured |
| — of which writes | 29%, being the four write request units an order costs to write transactionally |
| Every month, sent or not | ~$1.70 in alarms, plus $0.03 per GB of log retained over 30 days |
| Never billed | The queues, the tables, the function and the log group, for existing |

A month that carries a million events and an otherwise unspent free tier invoices about $7.80 for
both rows together, which is what the free tier is worth here rather than what the design costs.
Demonstration volume is pennies either way, and `OTEL_SDK_DISABLED=true` removes the largest of the
per-event lines for a run that does not need traces. The figure worth carrying is the monthly one,
because it arrives whether or not anything was sent. [Cost Model](docs/cost-model.md) derives all of
it, separates what was measured from what was estimated, and lists what changes the answer.

### Setting up an account

Once per account, with administrator credentials that nothing here stores and no workflow holds.
Node.js is needed for the CDK CLI, which is a pinned package with a committed lock file rather than
a global install.

```bash
cd infra/ReliableOrders.Cdk
npm ci
npx cdk bootstrap aws://<account>/<region>
npx cdk deploy ReliableOrders-DeploymentIdentity
```

The identity stack is deployed by hand because it decides who may deploy, and a stack the pipeline
could deploy is a stack the pipeline could widen. An account that already trusts GitHub's issuer has
a provider already — IAM allows one per issuer and refuses a second — so import that one instead of
creating another:

```bash
npx cdk deploy ReliableOrders-DeploymentIdentity -c githubOidcProviderArn=<arn>
```

It outputs three role ARNs, one per deploying environment. IAM decides which environment may assume
which role, and only GitHub can decide which ref may reach an environment, so the second half is a
script rather than a page of instructions — a control nobody can re-apply is one nobody can check.

```bash
cd ../..
./scripts/configure-deployment-environments.sh --execute \
  --dev-role-arn <DevelopmentDeploymentRoleArn> \
  --release-role-arn <ReleaseDeploymentRoleArn> \
  --e2e-role-arn <EndToEndRoleArn> \
  --region <region>
```

It writes through `gh`, which has to be installed and authenticated. Run it with no arguments first
and it prints what it would do without doing any of it. A fork has one line to change, the `REPO` at
the top: `gh` resolves a repository from the directory it runs in, so a run from a fork's clone
would otherwise create the environments here and report it as done.

From then on a push to `main` deploys the development stack on its own. [CI/CD
Design](docs/ci-cd.md#deployment-identity) covers the trust in full, including why no AWS access key
appears anywhere in this repository's configuration and why a pull request from a fork cannot reach
one.

### Deploying by hand

The CDK does not build the function. Synthesis packages what is on disk and fails naming the publish
command when there is nothing there, so the publish comes first.

```bash
dotnet publish src/ReliableOrders.Function -c Release
cd infra/ReliableOrders.Cdk
npx cdk diff ReliableOrders-dev
npx cdk deploy ReliableOrders-dev --outputs-file outputs.json
```

Both commands name their stack. The application holds two, and `cdk deploy` with none named refuses
to guess rather than reaching for the one that decides who may deploy.

The outputs file is how everything below finds the deployment. It carries the source queue URL, the
dead-letter queue URL, both table names, the function name, its log group and the dashboard. Take
them from the repository root, which is where the rest of this section runs.

```bash
cd ../..
OUTPUTS=infra/ReliableOrders.Cdk/outputs.json
QUEUE=$(jq -r '.["ReliableOrders-dev"].SourceQueueUrl' "$OUTPUTS")
DLQ=$(jq -r '.["ReliableOrders-dev"].DeadLetterQueueUrl' "$OUTPUTS")
ORDERS=$(jq -r '.["ReliableOrders-dev"].OrdersTableName' "$OUTPUTS")
LOGS=$(jq -r '.["ReliableOrders-dev"].OrderProcessorLogGroupName' "$OUTPUTS")
```

### The flows against a deployed stack

The same flows as above, and two differences that are not cosmetic.

**The samples are stamped, and a deployed function checks the stamp.** `occurredAtUtc` has to sit
within 24 hours ahead of processing time and five days behind it — the source queue's retention plus
a day, so a message that sat in the queue for its whole life still validates on its last delivery
attempt. The files in [`samples/`](samples/README.md) carry a fixed date and are older than that,
so a deployed run restamps them. The local stack does not, because `compose.yaml` widens the window
to a year for exactly this reason.

**A deployed stack keeps what the last run left.** `docker compose down -v` discards every row and
CloudFormation does not, so a sample sent unmodified a second time is the first run's event arriving
again — and with the order identifier changed but the event identifier kept, it is worse than that:
one identifier claimed by two payloads, which is the conflict this service alarms on. Each run below
takes both identifiers of its own, so a run starts from nothing without the tables having to.

The AWS CLI, `jq`, `uuidgen`, and credentials for the account the stack is in.

```bash
RUN=$(date -u +%s)
event() {
  jq -c --arg t "$(date -u +%Y-%m-%dT%H:%M:%SZ)" --arg order "ORD-$RUN" --arg id "$(uuidgen)" \
    '.eventId = $id | .occurredAtUtc = $t | .data.orderId = $order' "samples/$1"
}
```

`causationId` is left as the file has it, pointing at an event this run did not send. Nothing
classifies on it — it is a link for a reader, and the contract places no rule on it.

Each flow is read in the function's log, where every record ends in one line whose `LogEvent` field
is named for the outcome. Follow it in a second terminal.

```bash
aws logs tail "$LOGS" --follow --format short
```

**Valid, then duplicate.** One body sent twice, which is what an at-least-once redelivery is. The
`duplicate-order-created-v1.json` fixture is that same body on disk and is what the local flow
sends; here the variable is the simpler of the two, because a run rewrites identifiers and the file
would have to be rewritten in step with it.

```bash
VALID=$(event valid-order-created-v1.json)
aws sqs send-message --queue-url "$QUEUE" --message-body "$VALID"
aws sqs send-message --queue-url "$QUEUE" --message-body "$VALID"
aws dynamodb scan --table-name "$ORDERS" --select COUNT
```

`OrderCreated`, then `DuplicateIgnored` at `Event` scope, and one row either way. Inside ten minutes
of the first, the second says `OrderCreated` again instead: the transaction's `ClientRequestToken` is
the event identifier and DynamoDB replays the original result within that window. Both are correct,
and the claim that holds either way is that no second order is written.

**Republish, then conflict.** The same order under a new event identifier is a duplicate at order
scope rather than a conflict, which is the distinction the [Correctness
Model](docs/correctness-model.md) exists for. The conflicting sample differs from the republished one
in `amountMinor` and nothing else.

```bash
aws sqs send-message --queue-url "$QUEUE" --message-body "$(event republished-order-created-v1.json)"
aws sqs send-message --queue-url "$QUEUE" --message-body "$(event conflicting-order-created-v1.json)"
```

`DuplicateIgnored` at `Order` scope, then `IdempotencyConflict`, which is logged at error, raises an
alarm, and returns the record for redelivery until it dead-letters.

**Poison message.** The invalid event is sent as it is on disk. Restamping it would repair two of
the five rules it breaks on purpose, and it has no path forward regardless: it is returned on every
delivery and reaches the dead-letter queue after five receives, which on a deployed queue takes
about a quarter of an hour at the 210-second visibility timeout
[Infrastructure](docs/infrastructure.md#source-queue) derives.

```bash
aws sqs send-message --queue-url "$QUEUE" \
  --message-body file://samples/invalid-order-created-v1.json
aws sqs receive-message --queue-url "$DLQ" --max-number-of-messages 10 --visibility-timeout 0
```

The local stack shows the same flow in seconds, because redelivery there is immediate. It is the
faster place to watch the behaviour, and a deployed queue is the only place that demonstrates the
timeout the behaviour actually costs.

**Mixed batch.** Not reproducible by hand here, and saying so is more useful than a recipe that works
one time in three. A batch is whatever the event source mapping polls, and nothing outside the
account can stop it polling the way `docker compose stop mapping` does locally. The partial batch
response is asserted against a real deployment instead, by
[`A_bad_record_does_not_replay_the_batch`](tests/ReliableOrders.EndToEndTests/OrderProcessingTests.cs).

The dashboard is where all of this is meant to be read from in anger rather than one line at a time.
Open it by the `DashboardName` output, in the CloudWatch console for the Region the stack is in.
[Observability](docs/observability.md) covers what it shows, and the
[runbooks](docs/runbooks/dlq-investigation-and-redrive.md) cover what to do about it.

### Running the end-to-end tests against it

The suite reads the outputs file rather than asking CloudFormation, so pointing it at a deployment is
two variables. With neither set, every test in it skips and says why.

```bash
export E2E_OUTPUTS_FILE=$PWD/infra/ReliableOrders.Cdk/outputs.json
export E2E_STACK_NAME=ReliableOrders-dev
dotnet test --project tests/ReliableOrders.EndToEndTests -c Release
```

Nothing in the suite deploys or destroys anything. The scheduled workflow deploys a stack of its own,
points these tests at it, and destroys it in a step that runs whether they passed or not — [CI/CD
Design](docs/ci-cd.md#ephemeral-aws-end-to-end-test) covers that run, including what it does about
the stacks a cancelled one leaves behind.

### Tearing it down

```bash
cd infra/ReliableOrders.Cdk
npx cdk destroy ReliableOrders-dev
```

The development environment is configured to retain nothing, so this takes both tables and every row
in them, both queues, and the log group. That is right for an environment whose purpose is to be
redeployed, and it is exactly what an environment holding anything worth keeping must not do:
`RetainData` turns on deletion protection and a retain policy together, and the configuration refuses
to be constructed with retention enabled and point-in-time recovery off.

Two things survive, both deliberately. `ReliableOrders-DeploymentIdentity` is not named by that
command, because the roles outlive any one deployment of the stack they deploy. The CDK bootstrap
stack and its asset bucket belong to the account rather than to this project. Remove either only if
nothing else in the account is using it.

## Limitations and roadmap

What the first release does not do, said once rather than inferred from what is missing.

- **Ordering between orders is not guaranteed**, and cannot be on a standard queue. [ADR
  0001](docs/adr/0001-use-sqs-standard-queue.md) argues why that is the right trade here and what
  would have to be true to want FIFO instead.
- **One event type, one schema version.** `order.created` v1 is the whole contract. Versioning is
  designed for in [Event Contract](docs/event-contract.md) and not yet exercised by a second version.
- **No external side effects.** The worker writes to DynamoDB and to nothing else. Anything
  downstream of a created order needs the outbox this release does not have.
- **A conflict dead-letters.** There is no quarantine queue, so an event whose identity is claimed by
  contradictory data is retried its five times and lands beside the malformed ones.
- **One Region, one account.** No multi-Region processing, and one account holds the development
  stack, the ephemeral end-to-end stack, and the roles that deploy both.
- **Every trace is recorded.** No sampler is configured. That is defensible at this volume and it is
  the single largest line on the bill.
- **The publisher is a stub.** `src/ReliableOrders.Publisher` is scaffolded and exits saying so, which
  is why the flows above drive the queue with the AWS CLI.
- **Native AOT is not measured.** The managed runtime was chosen with the benchmark deferred rather
  than assumed — [ADR 0003](docs/adr/0003-use-dotnet-10-managed-runtime.md).
- **A Dependabot bump needs one human commit.** Central package management, per-project lock files
  and locked-mode restore interact so that a transitive bump leaves lock files behind. The workaround
  is in [CI/CD Design](docs/ci-cd.md#dependency-updates).

The roadmap is the backlog rather than a list here, and what is planned rather than declined is on
[M6: Advanced Reliability
Patterns](https://github.com/SalZaki/aws-dotnet-lambda-sqs-idempotency/milestone/6): a
permanent-failure quarantine queue, a transactional outbox, the performance and Native AOT
comparison, and multi-account delivery.

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
