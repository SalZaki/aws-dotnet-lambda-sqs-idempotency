# Threat Model

[Security Requirements](security.md) lists the controls the implementation must satisfy. This
document is the other half: what an attacker would do, what they would reach by doing it, and which
of those controls stops them. It is a document of its own rather than a section there because the
two have different lifecycles — a control changes when the stack changes, and a threat changes when
the system's boundaries do — and because a requirements list nobody can trace to a threat is a list
that only ever grows.

Six threats are covered, and they are the six the design has always named: malformed events, replay,
idempotency key reuse, resource exhaustion, logging leakage, and compromised CI. Each states an
attacker action, the asset it reaches for, the mitigation, and what is left over.

## Trust boundaries

| Boundary | Crossed by | What is on the far side |
| --- | --- | --- |
| Publisher to source queue | `SendMessage` under an IAM identity in the account | An event this service has not parsed, of any shape SQS accepts |
| Queue to function | The event source mapping, under AWS's control | A batch of up to ten records, each with an `ApproximateReceiveCount` this service does not set |
| Function to DynamoDB | `TransactWriteItems` under the execution role | Two tables holding every order this service has committed |
| GitHub Actions to the AWS account | An OIDC token minted per job, exchanged for a role | Whatever the CDK bootstrap's roles may create |
| Telemetry to its readers | CloudWatch Logs, metrics, traces, and public run summaries | Anyone with read access to the account, and for run summaries, anyone at all |

The publisher boundary is the one to keep in view. Nothing inside this service authenticates an
event: SQS authenticates the *sender* through IAM, and the queue grants no public or cross-account
send access, so "who may publish" is an account-level decision taken outside this repository. Every
threat below that begins with a message assumes an attacker who has already reached that far, which
is the only assumption under which the event contract is worth validating at all.

## Assets

- The committed orders — the amount, currency, customer and description of an order that has been
  accepted, and the guarantee that it is stored once.
- The idempotency records, which are not data anyone wants but are what the correctness claim rests
  on. An attacker who could delete them selectively could make a replay create a second order.
- Processing availability: the age of the oldest message, and the retry budget a poison message
  consumes on its way to the dead-letter queue.
- The AWS account, reached through the deployment roles rather than through anything the worker
  does at run time.
- The telemetry, which carries identifiers and, if the rules are broken, the contents of orders.

## Attacker profiles

| Profile | Reaches | Assumed to have |
| --- | --- | --- |
| A publisher gone wrong | The source queue | Legitimate `SendMessage` rights, and either a defect or intent |
| An outside contributor | A pull request, and every workflow it triggers | No secrets this repository holds, by GitHub's rules and by the workflow conditions |
| A reader of a public repository | The source, the workflow logs, and the run summaries | Nothing but a browser |
| A compromised dependency or action | Whatever the job that runs it holds | Execution inside CI, at the privilege of the job |
| A reader of the account's telemetry | Logs, metrics, traces | Read access granted for operations, not for the order book |

Out of scope: a hostile AWS operator, physical and side-channel attacks, and any threat that assumes
control of the account's root credentials. Multi-tenancy is out of scope because there is one tenant
— see [Non-Goals](overview.md#non-goals).

## 1. Malformed events

**Action.** The publisher sends a body that is not JSON, an envelope whose `schemaVersion` this
service does not know, a field padded with whitespace, an amount that is negative or absent, a
timestamp carrying a non-zero offset, or a description long enough to push the derived DynamoDB item
towards the 400 KB item ceiling.

**Asset.** The integrity of the orders table, and the retry budget: a message that fails permanently
is redelivered until `maxReceiveCount` is exhausted, and each attempt occupies a slot in a batch.

**Mitigation.** Parsing and validation are separate steps, and both run before anything is written —
the rules are in [Contract Rules](event-contract.md#contract-rules). An unsupported schema version is
refused rather than processed on the assumption that the parts this version understands are enough.
Padding is rejected rather than trimmed, because trimming would change the hash input and hide the
publisher's defect. Field length limits keep the worst-case item well under the item-size ceiling,
under an SQS message cap of 256 KB that bounds it again. Every one of these outcomes classifies as
[Permanent](correctness-model.md#error-classification): the record's identifier is returned, the good
records in its batch commit, and the message reaches the dead-letter queue rather than being retried
forever or dropped.

**Residual.** A poison message still burns five receives before it leaves, and during those receives
it occupies a slot in five batches. That is deliberate for V1 — a quarantine queue is
[Story 9.1](https://github.com/SalZaki/aws-dotnet-lambda-sqs-idempotency/issues/38) — and its
observable cost is bounded by the same retry policy that produces it. What the design refuses to do
is let the amplification reach the alarms: permanent-failure metrics are emitted only on first
receipt, so one bad message is one data point rather than five.

## 2. Replay

**Action.** An attacker who has captured a valid event, or a publisher whose retry logic has come
apart, sends the same event again — once, or a million times.

**Asset.** The effectively-once claim, and the bill. A replay that created a second order would
falsify the whole design; a replay that is correctly suppressed still costs a transaction attempt, a
log line and a trace.

**Mitigation.** The event-level idempotency key is the `eventId`, written under a condition that it
does not already exist, in the same transaction as the order. A replay cancels the transaction, and
the returned item's `EnvelopeSha256` is compared against the computed one: equal is a
[Duplicate](correctness-model.md#duplicate-and-conflict-classification), which is a success and not
an error. Nothing is overwritten on any path — both writes are conditional puts, and the execution
role holds `dynamodb:PutItem` and no other action, so there is no code path and no permission with
which this service could modify a committed order.

A replay that arrives after the idempotency record has expired is not unprotected: it falls through
to the order-level conditional write on `OrderId` and is classified on `BusinessSha256`, which is
why the order item carries a business hash of its own. TTL is cleanup, not the correctness boundary
— [TTL Is Cleanup, Not a Correctness Boundary](correctness-model.md#ttl-is-cleanup-not-a-correctness-boundary)
states the rule the code follows.

**Residual.** Replay is suppressed, not prevented, and suppression is not free — see
[What changes the answer](cost-model.md#what-changes-the-answer). A flood of duplicates is
processed correctly while `OrdersProcessed` stays flat, which is why alarm 7 is a composite over
queue depth and the *sum* of `OrdersProcessed` and `DuplicateEvents`: alarming on new orders alone
would fire on healthy duplicate traffic and stay silent through a replay storm.

## 3. Idempotency key reuse

**Action.** A publisher reuses an `eventId` for an event carrying different business data, or
submits a new event that claims an `orderId` already committed with a different amount, currency or
customer. Whether the intent is to overwrite a stored order or the cause is a publisher that
generates identifiers from a colliding source, the arriving message is the same.

**Asset.** A committed order — specifically the possibility of changing one after the fact, which is
the outcome an attacker would be reaching for.

**Mitigation.** Both writes are conditional, so neither reuse can overwrite anything: the worst case
is a cancelled transaction. Classification then decides what the cancellation *meant*, by comparing
the returned item's hash against the computed one. A matching hash is a duplicate; a differing hash
is a [Conflict](correctness-model.md#duplicate-and-conflict-classification), which is permanent,
alarmed through `IdempotencyConflicts`, and routed to the dead-letter queue with the runbook
[Idempotency conflict](runbooks/idempotency-conflict.md) to work from. The event-level check takes
precedence, so a repeated `eventId` carrying a different envelope is a conflict regardless of what
the order item says. `IdempotentParameterMismatchException` — the same token inside DynamoDB's
ten-minute window with a different body — maps to `Conflict` rather than to the transient default,
because a deterministic request body leaves no other explanation.

**Residual.** The first writer wins and the second is refused, which is the correct outcome and not
a silent one: a conflict is a high-severity log line, a metric, an alarm and a message an operator
has to triage. This service cannot tell a hostile reuse from a publisher defect, and does not try —
the runbook's first step is to find out which it was.

## 4. Resource exhaustion

**Action.** Flooding the queue faster than ten concurrent executions can drain it, or sending
messages large enough to make each batch expensive to process, with the goal of delaying legitimate
orders or of running up a bill.

**Asset.** Processing availability, measured as the age of the oldest message, and the account's
Lambda concurrency, which is shared with everything else deployed in it.

**Mitigation.** Reserved concurrency is set rather than left at the account default, and the event
source mapping's maximum concurrency is required to be less than or equal to it — an invariant
`EnvironmentConfig` enforces and a CDK assertion re-checks, so the event source cannot request more
executions than the function is allowed to use. A flood therefore becomes a backlog rather than an
outage in some other function. Batch size and the batching window bound how much a single invocation
takes on; the invocation deadline check returns unprocessed records instead of being cut off mid-work
by the Lambda timeout, so pressure produces retries rather than lost records. Message size is capped
by SQS at 256 KB and by the contract's field limits well below that. On-demand tables absorb a spike
without a capacity decision. Alarms 3, 4, 5 and 8 — oldest message age, throttles, transient
failures and deadline deferrals — are what make the backlog visible.

**Residual.** There is no rate limit at the queue, and none is proposed: SQS is the buffer, and the
mitigation is to absorb a flood rather than to refuse it. Cost scales with the flood, and the
per-million arithmetic in [Cost Model](cost-model.md) is what to multiply. Throughput sizing is
[Story 9.3](https://github.com/SalZaki/aws-dotnet-lambda-sqs-idempotency/issues/40); what exists
today is a bound, not a benchmark.

## 5. Logging leakage

**Action.** Reading what the system writes about itself — a public repository's workflow run
summaries, or CloudWatch Logs under an operations role — and recovering order contents, customer
identifiers, or the account's own identifiers from them.

**Asset.** Customer data inside events and inside the DynamoDB items a cancelled transaction returns,
and the account identifiers embedded in queue URLs and role ARNs.

**Mitigation.** The [Do not log](observability.md#do-not-log) list is explicit, and the paths most
likely to break it are closed by construction rather than by care. Exception messages are not
logged — the type and stack trace are — because an SDK exception message carries request bodies and
item contents. A condition-check failure logs the compared hashes, never the returned item. Metric
dimensions are restricted to `Service` and `Environment`, so no order, event, customer or message
identifier can become a dimension; span attributes carry the same three identifiers the log scope
does and nothing from the order. Deployments publish output *names* to the run summary and never
values, because a queue URL carries the account ID and a step summary on a public repository is
public, and the account-ID masking that covers a log does not cover that file. Log retention is 30
days on a log group the stack declares, which bounds how far back a reader can go.

**Residual.** `EventId`, `OrderId` and `CorrelationId` are in every log line by design — they are
what makes an incident traceable, and a log that omits them cannot be used. Anyone who can read the
log group can therefore correlate an order identifier with a time and an outcome, which is the
trade the design accepts. What they cannot recover from it is what the order was for or who placed
it.

## 6. Compromised CI

**Action.** Reaching the AWS account through the repository rather than through the account: opening
a pull request that triggers a workflow holding credentials, publishing a malicious version of a
dependency or an action the workflows use, or — with push access — adding a workflow that widens what
it is itself allowed to do.

**Asset.** The deployment roles, and through them whatever the CDK bootstrap permits; the release
artefacts; and the two secrets this repository holds, neither of which reaches an AWS account.

**Mitigation.** No long-lived AWS credential exists to steal: every deployment assumes a role through
OIDC with a token minted for one job. Each trust policy demands the audience and the subject
`repo:<owner>/<repo>:environment:<name>` by `StringEquals`, and the repository is parsed at synthesis
rather than interpolated, because a wildcard in that subject is a valid condition that deploys
without complaint. GitHub holds the other half — which ref may reach which environment — written by
`scripts/configure-deployment-environments.sh` so the control can be re-applied and checked.
Deployment identity lives in a stack deployed by hand, not in the stack the pipeline deploys, so a
workflow cannot widen the trust policy that admits it. Neither deployment workflow triggers on
`pull_request`, and `deploy-dev.yml` requires its triggering run to have succeeded, to have been a
push, to have been a push to `main`, and to have come from this repository — the last being the only
one outside a fork's control — with a test that reads both workflow files back. Actions are pinned
to immutable commit SHAs; `contents: read` is the default and `id-token: write` is granted only where
a deployment needs it. On the supply-chain side, `NuGetAudit` fails the restore at audit level `low`
across the whole transitive graph, dependency review covers the two ecosystems restore never sees,
CodeQL runs over `actions` and `csharp`, and a weekly workflow re-resolves the pinned emulator
digests that no ecosystem parses. A release verifies that GitHub reports the tag's signature as
valid, and deploys the commit that job emitted rather than resolving the tag again on the far side
of the approval gate.

**Residual.** What a deployment may create is the bootstrap's decision, narrowed by a permissions
boundary where one is configured — and configuring one is a recommendation this repository makes
rather than a control it applies to an account it does not own. An account with push access to `main`
can deploy to `dev` without review, by design; `release` is what carries a reviewer and a signed tag.
A dependency compromised between two audit runs executes at the privilege of the job that restores
it, which for the gate is a job with no credentials and no account.

## Residual risk register

| # | Threat | Left over | Closed by |
| --- | --- | --- | --- |
| 1 | Malformed events | A poison message consumes five receives before it dead-letters | [Story 9.1](https://github.com/SalZaki/aws-dotnet-lambda-sqs-idempotency/issues/38), a quarantine queue |
| 2 | Replay | Suppression costs a transaction, a log line and a trace per replayed event | Nothing planned; the cost is the mechanism working |
| 3 | Key reuse | Conflicts are detected and triaged by hand, not prevented | Nothing planned; prevention belongs to the publisher |
| 4 | Resource exhaustion | A flood becomes a backlog and a bill, bounded but not refused | [Story 9.3](https://github.com/SalZaki/aws-dotnet-lambda-sqs-idempotency/issues/40), sizing against measurements |
| 5 | Logging leakage | Order and event identifiers are readable by anyone who can read the log group | Nothing planned; the identifiers are what make an incident traceable |
| 6 | Compromised CI | No permissions boundary is applied to accounts this repository does not own | The account owner, at bootstrap |

## When to read this again

This model assumes the system in [Overview](overview.md), and three planned changes would invalidate
parts of it rather than extend them.

- **An external side effect.** The moment anything is called that is not one of the two DynamoDB
  writes, replay stops being suppressed by the transaction and threat 2 needs rewriting around the
  outbox — see [External Side Effects](correctness-model.md#external-side-effects) and
  [Story 9.2](https://github.com/SalZaki/aws-dotnet-lambda-sqs-idempotency/issues/39).
- **A second publisher.** The queue admits one event type today, and "who may publish" is an
  account-level decision. A second source makes that a decision this repository has to state.
- **A second account.** [Story 9.4](https://github.com/SalZaki/aws-dotnet-lambda-sqs-idempotency/issues/41)
  adds a trust relationship between accounts, which is a boundary this table does not have a row for.
