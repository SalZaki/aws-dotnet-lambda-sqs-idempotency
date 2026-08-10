# Revision Log

Changes from specification v1. Each entry states what changed and why.

Entries 1 to 45 cite the section numbers used by the single-file specification. Those numbers no
longer exist. Change 46 split the document, so anything before it should be read against the
document names in [the index](README.md) rather than looked up by number.

## Blocking corrections

| # | Change | Sections | Rationale |
| --- | --- | --- | --- |
| 1 | Split the single `PayloadSha256` into `EnvelopeSha256` and `BusinessSha256` | 5.4, 5.5, 6.2, 6.3, 9.5, 9.6, 10.4 | v1 hashed "the event" but required a republished order under a new `eventId` to classify as a duplicate. One hash cannot do both: with envelope fields in scope, every legitimate republish became a conflict routed to the DLQ with a high-severity alarm. |
| 2 | Fixed `IdempotencyKey` as the verbatim `eventId`; removed `EntityType` and `EntityId` | 5.6, 9.6 | v1 never specified the key's value, and the two extra attributes implied a second idempotency row that the two-item transaction never writes. |
| 3 | Replaced the visibility-timeout worked example with an explicit formula computed in CDK | 9.1, 18.4, 21 | v1's table summed to 181 or 211, never the stated 210, and CDK Tests required a test against a formula that was never written down. |
| 4 | Documented the 36-character `ClientRequestToken` limit and mapped `IdempotentParameterMismatchException` | 5.6, 10.6, 11 | A bare UUID fits exactly; any prefix overflows. The exception was unmapped in v1 and fell through to "transient by default", burning all five retries on a permanent condition. |
| 5 | Introduced `IncomingMessage` and removed `SQSEvent.SQSMessage` from core interfaces | 10.1, 10.7, 19, 18.6 | v1's `IOrderMessageProcessor` contradicted its own layering rule and would have failed the architecture test it also specified. |

## New finding surfaced while folding

| # | Change | Sections | Rationale |
| --- | --- | --- | --- |
| 6 | Required transaction request bodies to be a pure function of the event; `ExpirationEpochSeconds` and `CreatedAtUtc` now derive from `occurredAtUtc`, and `IOrderCommandStore` no longer takes a clock | 5.6, 9.5, 9.6, 10.5, 20 | This falls directly out of correction 4. A deterministic `ClientRequestToken` combined with a wall-clock TTL or `CreatedAtUtc` means two attempts milliseconds apart build different request bodies, and DynamoDB rejects the second with `IdempotentParameterMismatchException` — turning a routine retry of a valid event into a hard error. v1 passed `DateTimeOffset now` into the store, which made this near-certain. |

## High-value corrections

| # | Change | Sections | Rationale |
| --- | --- | --- | --- |
| 7 | Adopted `ReturnValuesOnConditionCheckFailure = ALL_OLD` and removed the post-cancellation read | 5.3, 5.5, 10.6 | Saves a round-trip on the most common retry path and closes a TOCTOU window that v1 left unspecified. |
| 8 | Added the null-`Item` rule: classify as transient, never infer duplicate or conflict | 5.5, 11, 18.1 | TTL can sweep the conflicting record between condition evaluation and response. |
| 9 | Gated permanent-failure metrics on `ApproximateReceiveCount == 1` | 11.1, 13, 15 | `maxReceiveCount = 5` meant one poison message emitted five data points against a "greater than zero" alarm. |
| 10 | Required `SQSBatchResponse` registration in the serializer context, with a serialization round-trip test | 10.9, 18.1, 20 | An unregistered response type serialises to `{}`, which Lambda reads as an empty failure list — every failed record silently deleted, no error logged, and object-level unit tests all green. |
| 11 | Forbade null, empty, and duplicate `itemIdentifier` values | 10.8, 18.1 | An unrecognised identifier makes Lambda reprocess the entire batch, converting a one-record failure into a ten-record replay. |

## Medium corrections

| # | Change | Sections |
| --- | --- | --- |
| 12 | Split integration testing: `amazon/dynamodb-local` for transactions, LocalStack for SQS only | 8, 18.3, 6.2 |
| 13 | Made OTel and X-Ray active tracing mutually exclusive; set realistic expectations for .NET auto-instrumentation | 9.3, 14, 18.4 |
| 14 | Added `DeadlineDeferred` as a distinct outcome and documented receive-count burn from deadline pressure | 10.7, 10.8, 11, 15 |
| 15 | Removed `Outcome` as a metric dimension | 13 |
| 16 | Added CloudWatch Logs ingestion and 2× transactional write cost to the cost model | 13 |
| 17 | Moved the Lambda runtime identifier into `EnvironmentConfig` | 8.1, 9.3, 21 |

## Minor corrections

| # | Change | Sections |
| --- | --- | --- |
| 18 | Fixed heading hierarchy throughout; epics became H3 rather than H1 (section since removed, see change 32) | all |
| 19 | Defined `ParseResult`, `ValidationResult`, `MessageProcessingResult`, and `ProcessingContext` | 10.2, 10.3, 10.7 |
| 20 | Unified result modelling on closed record hierarchies with `private protected` constructors | 10.2, 10.5, 20 |
| 21 | Clarified that the ESM ignores `ReceiveMessageWaitTimeSeconds` | 9.1 |
| 22 | Stated the consequence of tolerating unknown fields — they are excluded from both hashes | 6.1, 18.1 |
| 23 | Made the UTC rule testable (`Offset == TimeSpan.Zero`) and added a configurable skew window | 6.1, 10.3 |
| 24 | Reconciled the repository name to `aws-dotnet-lambda-sqs-idempotency` | 19, 22.1 |
| 25 | Added the `MaxConcurrency <= ReservedConcurrency` invariant with a CDK assertion | 9.4, 18.4, 21 |

## Delivery plan changes

| # | Change | Sections |
| --- | --- | --- |
| 26 | Story 1.3 expanded to settle keys, scopes, and hashes before any table schema is written; ADR 0005 added | 19, backlog |
| 27 | New Story 2.0 pulls the DynamoDB container harness forward from Epic 6, because Story 2.3's acceptance criteria are otherwise unevaluable | backlog |
| 28 | Added the republish scenario to samples, E2E tests, demo assets, and the DoD | 17.4, 18.5, 19, 22, 24 |

## Backlog audit (specification v2.1)

Found by auditing the created GitHub issues against the spec's own deliverables.

| # | Change | Sections | Rationale |
| --- | --- | --- | --- |
| 29 | Added Story 8.3 (architecture, threat, cost, and testing documents) and Story 8.4 (ADRs 0001–0004) | backlog | Security Requirements required a threat model, Metrics Specification required a cost model, and Repository Structure listed four documents and five ADRs — but no story owned any of them. Milestone 5's description promised "Architecture decisions, Threat and cost models" while containing only three stories, none of which produced them. Only ADR 0005 had an owner, via Story 1.3. |
| 30 | Added acceptance criteria to Stories 9.1–9.4 | backlog | They were the only four of thirty-one stories with tasks but no criteria, so nothing could be objectively closed. Being post-V1 is a reason to defer them, not a reason to leave them unfalsifiable. |
| 31 | Named the owning story against each Milestone 5 line item | backlog | Makes the milestone auditable against the backlog rather than aspirational. |

## Backlog moved out of the specification (v3)

| # | Change | Sections | Rationale |
| --- | --- | --- | --- |
| 32 | Removed the Epics and User Stories section and the Suggested Delivery Sequence section. Replaced them with Delivery, a pointer to the GitHub backlog. Renumbered the two sections that followed. | Delivery, Definition of Done, Final Positioning | The two sections were 609 lines, 30% of the document, restating a backlog that GitHub now holds with state, ownership, dependencies, and progress that markdown cannot represent. They had already drifted twice: Stories 8.3 and 8.4 had to be written in both places, and the Epic 8 checklist went stale as soon as they were added. A specification and a plan have different lifecycles, and keeping both in one file guarantees one of them is wrong. |
| 33 | Moved the two load-bearing ordering constraints into the design sections that argue them | 5.4, 18.3, 23 | Story ordering that follows from a design decision belongs beside the decision. Ordering that is merely scheduling belongs in the tracker. |

## Editorial pass and review findings (v3)

| # | Change | Sections | Rationale |
| --- | --- | --- | --- |
| 34 | Removed every section symbol in favour of named references, and every colon outside code, tables and links | all | Section numbers break silently when sections move, and the renumbering in change 32 had already invalidated some. Names survive it. |
| 35 | Promoted 29 standalone bold labels to headings at one level below their parent | all | They were headings in everything but markup, which `MD036` correctly flagged. The document now runs H1 to H4 with no skipped level. |
| 36 | Adopted markdownlint with a committed configuration and a CI workflow, and hard-wrapped prose at 100 columns | all | Formatting drift is cheaper to prevent than to argue about. Every non-default rule carries a comment saying why. |
| 37 | Renamed the file from `spec.md` to `system-specification.md` | 19 | The old name said nothing about which of the planned documents it was. |
| 38 | Repaired eight sentences broken by the colon removal, and one duplicated word left by a section-name substitution | 4, 5.8, 6.2, 10.9, 12, 13, 15, 22, 24 | Mechanical edits produced ungrammatical prose such as "The recommended extension is." Found by re-reading the whole document rather than by the linter, which cannot see it. |
| 39 | Declared `OrderCreatedV1` and `OrderData` | 6 | Every other type crossing a component boundary was defined in full, but the one the entire contract turns on appeared only in signatures. |
| 40 | Required committed known-answer hash vectors | 6.3, 18.1 | The previous test compared two hashes computed in the same process, which move together when the serializer changes. Only a fixed expected digest catches a canonicalisation change on a runtime upgrade, and such a change silently reclassifies every replay as a conflict. |
| 41 | Added a contract rule for `causationId` | 6.1 | It sits inside `EnvelopeSha256` and therefore affects classification, but no rule said whether it was required or nullable. |
| 42 | Removed the `Status` attribute from the idempotency record | 9.6 | It can only ever hold one value under a transactional design. A status field exists to distinguish an in-flight claim from a completed one, which is the mark-then-write pattern this specification rejects. |
| 43 | Widened alarm 7 to the sum of `OrdersProcessed` and `DuplicateEvents` | 15 | Alarming on new orders alone fires during a healthy replay storm, where every record is processed correctly and no new order is created. |
| 44 | Fixed the handler style as a class library, not an executable assembly | 10.9 | The composition root said "constructor or executable startup code", leaving a decision that changes the entry point unmade. |
| 45 | Required every alarm threshold to carry a concrete value before the [Definition of Done](delivery.md#definition-of-done) | 15 | Several read "an agreed threshold" with no number and no owner. |

## Split into per-topic documents (v3)

| # | Change | Documents | Rationale |
| --- | --- | --- | --- |
| 46 | Split the single 1716-line specification into twelve per-topic documents plus an index at `docs/README.md` | all | The repository layout already planned `architecture.md`, `correctness-model.md` and `testing-strategy.md` as separate files, and Story 8.3 was tasked with writing them. Leaving one large file guaranteed that story would copy sections into those names, recreating the duplication that change 32 removed. Splitting now makes the planned filenames the only home for that content. |
| 47 | Converted every cross-reference from a bare section name into a markdown link | all | Named references survived renumbering but gave the reader nothing to click, and across files they would have been unusable. All internal links are checked for a resolving file and anchor. |
| 48 | Renamed two headings that duplicated their file title | architecture, delivery | `High-Level Architecture` inside `architecture.md` became `System Diagram`, and `Delivery` inside `delivery.md` became `Backlog`. |

## Front door and licence (v3)

| # | Change | Documents | Rationale |
| --- | --- | --- | --- |
| 49 | Made the root `README.md` the project front door and reduced `docs/README.md` to an index of its own folder | README, docs index | The root file was a single heading, so the repository homepage was empty while the documentation index carried the project title, blurb and contents. Two READMEs is conventional. Having the front door empty is not. |
| 50 | Moved the skills inventory out of `overview.md` into the root README | overview, README | It is portfolio positioning rather than specification. A visitor meets it on the homepage; a reader of the design documents does not need it. |
| 51 | Replaced the README checklist in `delivery.md` with a pointer to the story that owns it | delivery | A checklist describing a deliverable, and the story implementing that deliverable, drift apart. Same failure that moved the epic and story listings out of this specification. The bullets are now acceptance criteria on the README story. |
| 52 | Chose MIT and added `LICENSE` | infrastructure | The technology table had said "Apache-2.0 or MIT; choose before the first public release" since v1. The repository was already public with no licence file, so nobody could legally reuse it. |
| 53 | Fixed a blockquote in the documentation index that swallowed the status line | docs index | The split removed the blank line after the blockquote, so markdown lazy continuation absorbed the following paragraph into the quote. |
| 54 | Stated the scope boundary at the top of `security.md` | security | It covers design-time controls. Threats and attacker profiles belong in the threat model, which is not written yet. |

## Structured logging (Story 5.1)

| # | Change | Documents | Rationale |
| --- | --- | --- | --- |
| 55 | Moved `Outcome` and `DurationMs` off the record scope and onto each record's terminal event | observability | A scope is opened before the work it covers, and neither value exists until that work has finished. Listing them as scope fields asked for something no ordering can supply. Every record reaches exactly one terminal event, so a query grouping by `Outcome` is unaffected. |
| 56 | Reserved `EventId` for the publisher's identifier and named the logging framework's event number `LogEventId` | observability | Both wanted the same field. The framework writes a small integer naming which log statement was reached; the specification's `EventId` is the order event's UUID. Sharing the name would have left every parsed line reporting the wrong one, with nothing looking broken. |
| 57 | Required log lines to be written flat rather than with scopes nested in an array | observability | The order identity scope opens only after a body parses, so a nested field sits at a different array index on a parse failure than on a success. One Logs Insights query cannot match both, which would have made "queryable by event, order, correlation and SQS message ID" true of only some lines. |
| 58 | Excluded exception messages from log output, keeping the type and stack trace | observability | The Do Not Log list already forbade full exception payloads containing message bodies, and an AWS SDK exception message is where a request body or an item's contents arrives from outside this service's own statements. |

## EMF metrics (Story 5.2)

| # | Change | Documents | Rationale |
| --- | --- | --- | --- |
| 59 | Added a `PermanentFaults` metric | observability | The table had no metric for a permanent fault in this service, so `permanent.table-not-found` and `permanent.access-denied` produced none at all — the message exhausted its retries and dead-lettered with nothing to alarm on. Folding them into `ValidationFailures` was the alternative and is worse than the gap: it points the validation and conflict runbooks at a publisher when the cause is a missing IAM action or a wrong table name. `WriteFailureReason` already draws the same line. |
| 60 | Aggregated metrics per invocation into one EMF record rather than emitting one per message | observability | The specification's own cost note makes per-record EMF the dominant cost of the project at any real volume, and EMF's array-of-values support exists for this. Publishing on disposal keeps the aggregate reportable when an invocation throws. Records beyond EMF's limit of 100 values against one metric carry the overflow samples and no counters. |
| 61 | Omitted zero-valued counters from published records | observability | The gate's acceptance criterion is that one poison message produces exactly one validation-failure data point across five deliveries. Publishing a zero on the other four satisfies the gate while producing five data points, so omission is what makes the criterion literally true. `BatchSize` and `BatchFailures` are excepted, because a continuous failure series is what makes a partial batch failure legible. |
| 62 | Required the CloudWatch namespace to be supplied by the composition root | observability | The metrics table named no namespace, leaving a value every metric is published under undecided. It is a deployment value rather than a constant, and Story 3.3 validates configuration at cold start. |

## Review corrections (Stories 5.1 and 5.2)

| # | Change | Documents | Rationale |
| --- | --- | --- | --- |
| 63 | Added a `PermanentProcessingFailure` log event | observability | Change 59 added the `PermanentFaults` metric but left the event list unchanged, so a permanent fault had a metric and no matching event. The nearest event was `TransientProcessingFailure`, which would have stamped a retryable outcome on something no retry can fix and left the alarm and the log disagreeing while an operator waited for a downstream service to recover from a missing IAM action. |
| 64 | Exempted `OrdersProcessed` and `DuplicateEvents` from the zero-omission rule of change 61 | observability | Alarm 7 is a composite over queue depth and the sum of the two. With both omitted at zero, the sum has no datapoints during a total processing outage, so the alarm reports insufficient data instead of firing — the one case it exists for. Neither is gated, so the exemption costs the first-receipt guarantee nothing. |
| 65 | Required log lines to be written synchronously rather than through a queued provider | observability | Lambda freezes the execution environment when the handler returns, so a queued line waits for the next thaw and is lost if the environment is reclaimed. The lines at risk are the last ones written, which is where `BatchCompleted` lands — the only place in the log where an invocation returning batch item failures admits something went wrong. It also keeps log lines and EMF records in the order they happened, since the metrics publisher writes synchronously. |
| 66 | Recorded that `ProcessingDeadlineReached` carries no `DurationMs` | observability | Change 55 said every terminal event carries one. A deferral did no work, and a zero would drag down any latency derived from that field exactly when the handler is under most pressure. The metrics side already excluded deferrals from latency for the same reason. |

## Message processing (Story 3.1)

| # | Change | Documents | Rationale |
| --- | --- | --- | --- |
| 67 | Removed `ProcessingContext` from `IOrderMessageProcessor`, which now takes the invocation's `IInvocationMetrics` | architecture | The context carried a Lambda request identifier, a service name and an environment. Story 5.1 left none of the three for a processor to hold: the service and environment belong to `ProcessingLog`, which owns them for the process, and the request identifier is on the invocation scope the batch handler opens. A processor taking them would be a second source for values it never reads. What it does need is the invocation's metrics, which is a collaborator rather than context. |
| 68 | Added `ParseFailureReason.UnsupportedSchemaVersion` | event contract | `ParseResult.UnsupportedSchemaVersion` had no reason string, so the one parse outcome that means "deploy a newer build" had nothing to log or count itself as. The version number is deliberately not folded into the value, because a reason is what a metric groups by and every unsupported version would otherwise get its own series. |
| 69 | Made the deadline check the batch handler's rather than the processor's | architecture, testing strategy | `MessageProcessingOutcome.DeadlineDeferred` is defined by Story 3.1 and produced by Story 3.2. Only the handler knows the invocation's remaining time, and giving the processor a deadline would have meant a second clock on a path whose whole purpose is to be reproducible. Case 24 in the coverage table now waits on `SqsBatchHandler` rather than on the processor. |

## Partial batch response (Story 3.2)

| # | Change | Documents | Rationale |
| --- | --- | --- | --- |
| 70 | Moved the `SQSEvent.SQSMessage` to `IncomingMessage` mapping into `ReliableOrders.Aws/Sqs/`, beside the handler | architecture | The Repository Structure section said the Lambda project owns the mapping, while the `SqsBatchHandler` section lists mapping as that handler's first responsibility and the structure gives `ReliableOrders.Aws/Sqs/`. The two readings could not both hold. A step of the handler is not a composition-root concern, and the layering rule the first sentence exists to protect — that Core sees no AWS package — is satisfied either way. |
| 71 | Gave the handler an absolute deadline rather than an `ILambdaContext` | architecture | It needs one fact from that interface, when to stop, and depending on the whole of it would make every batch test construct a Lambda context. Turning remaining time into a deadline is the composition root's job, and `ProcessingDeadline.From` is where the margin is applied and argued. |
| 72 | Recorded that the deadline margin is provisional until a p99 exists | architecture | The specification says to size it against observed p99 per-record latency and nothing has run yet, so the default is a starting point rather than a measurement. It is documented as something Story 6.3's end-to-end tests must replace, not as a justification for keeping it. |

## Composition root (Story 3.3)

| # | Change | Documents | Rationale |
| --- | --- | --- | --- |
| 73 | Corrected what an unregistered response type does | architecture | Changes 10 and the Composition Root section both said it serialises to `{}` and that Lambda would delete every failed record silently. Verified against `Amazon.Lambda.Serialization.SystemTextJson` 3.0.0 by removing the registration: serialising throws `JsonSerializerException` naming the type. The registration is still required and the test still reads bytes — a shape change writes valid JSON that matches no record — but the failure is a deployment that breaks on its first message rather than silent data loss, and the specification should not claim a danger the runtime does not have. |
| 74 | Named `METRICS_NAMESPACE` as an environment variable | infrastructure | Change 62 made the CloudWatch namespace a composition-root value without saying where it comes from. |
| 75 | Split the environment variables into required and optional, and stated what an unusable value does | infrastructure | The list gave no indication which have defaults. Table names, service, environment and namespace are required because nothing sensible can be assumed for them; a value that is set but unparseable fails the cold start naming itself rather than falling back, because defaulting over it would run the service on a number nobody chose while the deployment appeared to take effect. |

## Messaging resources (Story 4.1)

| # | Change | Documents | Rationale |
| --- | --- | --- | --- |
| 76 | Gave both queues explicit names | infrastructure | The dead-letter queue's redrive allow policy has to name the source queue, and naming it by resource reference makes each queue depend on the other — a circular dependency CloudFormation rejects, which is what the "where supported" hedge in v1's redrive row was standing in for. With the name fixed, the policy composes the ARN from the stack's own partition, Region and account and reaches the same queue without referencing the resource. The cost is that one account and Region holds one deployment per environment, which the source queue section now states. |
| 77 | Added a TLS-only deny statement to both queue resource policies | infrastructure | The resource policy row said only what must not be allowed. Transport was unstated, so a queue satisfying every stated rule still accepted plaintext calls, and the statement costs one property. |
| 78 | Recorded that the environment is a CDK context key, and that an unknown one fails synthesis | infrastructure | The design said configuration is environment-based and typed without saying how an environment is selected. Falling back to the development configuration on an unrecognised name would deploy development retention and concurrency into whichever account the credentials pointed at. |
| 79 | Retained the dead-letter queue where the tables are retained | infrastructure | The removal policy row covered the tables alone, so the dead-letter queue took CDK's default and a destroy — or a change CloudFormation implements as a replacement — discarded the one thing in the stack that cannot be reconstructed. The source queue is deliberately not retained with it: a retained queue keeps its physical name and blocks the next deployment, which is a price worth paying for undiagnosed failures and not for a queue that refills on its own. |
| 80 | Required `CDK_DEFAULT_ACCOUNT` and `CDK_DEFAULT_REGION` rather than passing them through | infrastructure | Unset, both are null and CDK synthesises an environment-agnostic stack that deploys into whichever account is supplied later. That is the outcome the design's own note on avoiding hard-coded account IDs assumes is impossible, and it is silent — the template synthesises, and only the ARNs it renders as pseudo-parameters show what happened. |
| 81 | Adopted the recommended CDK feature-flag set in `cdk.json`, and made the CDK tests synthesise with it | infrastructure | A flag absent from context takes the pre-flag behaviour, and adopting it later changes logical IDs and forces replacement of resources by then deployed. Nothing is deployed yet, so the cost is zero today and rises with every story. The tests read the same file rather than a bare context, because a suite asserting the pre-flag template verifies a shape no deployment produces. |
