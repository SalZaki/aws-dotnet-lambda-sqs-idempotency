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

## Persistence resources (Story 4.2)

| # | Change | Documents | Rationale |
| --- | --- | --- | --- |
| 82 | Left both tables unnamed and published their names as outputs | infrastructure | The queues carry physical names because a redrive allow policy has to name one of them. No table has that constraint, and a named table cannot be replaced without a collision, so the asymmetry is deliberate and now stated rather than left to look like an oversight. |
| 83 | Specified the AWS-managed KMS key rather than "encryption enabled" | infrastructure | DynamoDB encrypts at rest either way, so the row as written described no decision. The AWS-owned default records no key use in CloudTrail, which is the difference that matters on a table holding customer identifiers and amounts, and it emits no `SSESpecification` for a CDK assertion to read. The managed key is not customer-managed, so the execution role still needs no KMS permissions. It is not free, though: unlike the AWS-owned key it bills a KMS request against table traffic, which on this design is every order written. The cost model has to carry that line. |
| 84 | Fixed the table grant at `dynamodb:PutItem` on the two table ARNs | infrastructure, security | The story asked for least privilege without saying what that is here. It is one action. A transactional write is authorised by the actions of the items inside it, and the classification path reads the old image out of the cancellation reason, so no read action is needed — `GrantWriteData` would have added update, delete and batch write for nothing. |
| 85 | Disabled test parallelisation in the CDK suite | testing strategy | Every CDK call crosses into one node process through jsii. A second test class synthesising concurrently returned results belonging to other tests, failing 22 of 38 cases that each pass when their class runs alone, and then hanging. Found when the second construct's tests were added, which is the first time two classes in that assembly synthesised at once. |

## Lambda and event source mapping (Story 4.3)

| # | Change | Documents | Rationale |
| --- | --- | --- | --- |
| 86 | Made the deployment package an input to synthesis rather than something CDK builds | infrastructure | Bundling during synthesis needs Docker or a matching SDK wherever the app is synthesised, and the pipeline already builds and tests the solution before deploying, so a second publish would deploy a binary nothing tested. The cost is that a forgotten publish would silently package an empty directory, so an absent or empty publish directory now fails synthesis naming the command. |
| 87 | Set the Lambda log format to text rather than JSON | infrastructure, observability | The function writes its own structured JSON and its own EMF records. Lambda's JSON log format wraps each stdout line in an envelope of its own, which puts the EMF record's `_aws` key below the root — CloudWatch extracts metrics only from `_aws` at the root, so every custom metric would stop being published while the log line carrying it still looked correct. It would also defeat the fixed top-level paths `FlatJsonConsoleFormatter` exists to guarantee. |
| 88 | Fixed log retention at 30 days on a log group the stack declares | infrastructure | The row said "explicitly configured" without saying what. A function left to make its own group gets one that never expires and bills for it forever, and the group has to exist in the template for the removal policy to follow the data. |
| 89 | Set only the required environment variables, and stated that the optional ones stay unset | infrastructure | The list did not say who sets what. Restating an optional default in the deployment puts a second copy of it where it outranks the value `FunctionConfiguration` argues for, and the two drift with nothing to reconcile them. |
| 90 | Recorded that the service name and metrics namespace are stack constants | infrastructure | Both are required of the function and neither varies between deployments of the same service — the environment is what distinguishes those and is already its own variable and its own metric dimension. |
| 91 | Bounded batch size against the batching window | infrastructure | SQS allows ten records per batch without a batching window and ten thousand with one, and CDK stops checking the ceiling as soon as a window is defined — including a window of zero seconds, which is the case that means no window at all. An oversized batch synthesised cleanly and was rejected at deploy, which is the class of failure `EnvironmentConfig` exists to move forward to construction. |
| 92 | Corrected the Architecture row to say what is deployed | infrastructure | It read "Configurable, benchmark ARM64 against x86_64", and neither the construct nor `EnvironmentConfig` carries an architecture, so every deployment is x86_64 and nothing can vary it. The row described an intention as though it were implemented, which is worse than an absent row now that the table beside it is accurate. |
| 93 | Added the publish step to all three documented pipelines | ci/cd | Synthesis packages the publish output rather than building it, and every step list ran a CDK command on a clean checkout with nothing to package. The end-to-end teardown matters most: `cdk destroy` synthesises before it deletes, so a cleanup step without the publish output fails and orphans the stack it exists to remove. The workflow that deploys has not been written yet, so this is a break in the document it will be written from rather than a break in CI today. |
| 94 | Enabled event-source event-count metrics | infrastructure, observability | The specification asked for them "where available" without confirming they were. They are, through the event source mapping's metrics configuration, and without them the only view of the source is the queue's own metrics, which cannot tell a poller that stopped reading from a queue that stopped filling. |

## Container integration suite (Story 6.2)

| # | Change | Documents | Rationale |
| --- | --- | --- | --- |
| 95 | Recorded that LocalStack now requires an auth token | testing strategy, ci/cd | The design chose LocalStack for SQS when it needed no account. Its community and pro images merged in 2026.3.0, and since 6 April 2026 the image exits with code 55 before opening its edge port without `LOCALSTACK_AUTH_TOKEN`. The free tier still covers what these tests do, but the requirement reaches the workflow, the fixture and anyone cloning the repository, so it cannot stay unwritten. Nothing about the emulator split changes: DynamoDB stays on `amazon/dynamodb-local` for the reasons already given. |
| 96 | Declined those tests in two places rather than one | testing strategy, ci/cd | GitHub does not expose repository secrets to a pull request from a fork, so an outside contributor never has a token however the repository is configured. Without a token the tests skip with a reason, which keeps an IDE run at seventeen passed and eight skipped rather than eight red; the workflow additionally excludes them by trait, which reaches the same verdict without first pulling a two-gigabyte image for tests that were never going to run. Skipping alone was not enough, and excluding alone left the IDE broken. |
| 97 | Recorded that a TLS interceptor blocks licence activation, and added `LOCALSTACK_CA_BUNDLE` | testing strategy | Activation is an HTTPS call, and a corporate interceptor re-signs it with a certificate the container has no reason to trust — reported as a licensing server that cannot be reached, which reads as an outage rather than a trust failure. The variable is read from the environment because the certificate belongs to the network, not the repository. The trap is recorded with it: Zscaler's locally installed root has been seen without its basic constraints marked critical, which OpenSSL 3 rejects outright, so mounting the host copy changes the error rather than fixing it while the published root works. `SSL_NO_VERIFY` is deliberately set nowhere. |
| 98 | Documented the integration workflow | ci/cd | It has existed since Story 2.0 and the CI/CD design never mentioned it, so the only description of what runs in it was the file itself. It now carries a secret, a conditional pull and a timeout, none of which a reader could infer. |
| 99 | Corrected the pull-request gate's step list | ci/cd | Step 7 said the gate runs integration tests. It does not and never did — `ci.yml` filters on `Category!=Integration`, which is the whole reason the integration workflow exists. A step list that claims a check nobody runs is worse than no list. |
| 100 | Stood in for the event source mapping in tests, and said what that does not model | testing strategy | A batch response only means something against the queue it came from: a response of the right shape still replays nine records if the mapping cannot match its identifiers, and no unit test can show that. The stand-in deletes what the response does not name and returns what it does. It models nothing else — batch window, concurrency, and an invocation that fails outright belong to Story 6.3 — and saying so is what stops it being read as a claim about Lambda. |
| 101 | Bounded the container waits and the integration job | testing strategy, ci/cd | The interesting failure never succeeds. An expired or rejected token exits the container, no wait condition is ever met, and the Testcontainers default spends an hour before reporting a timeout that names a container start rather than a dead licence. |
| 102 | Stated the prerequisites for running the suite | README | The README said the .NET SDK was the only one. Node.js has been required since the first CDK test — jsii runs it as a child process, and without it those tests fail on a missing executable rather than on anything they assert — and Docker since the first container test. Both were true before this story and neither was written down. |

## Client-timeout classification (#59)

| # | Change | Documents | Rationale |
| --- | --- | --- | --- |
| 103 | Made the store's cancellation guard check the token | testing strategy | `TaskCanceledException` derives from `OperationCanceledException`, and an AWS SDK client-side HTTP timeout raises one with nothing cancelled. Matching on the type alone rethrew a socket timeout as cancellation, against a store whose contract is to report failure by returning `OrderWriteResult`. The handler contained it — the record came back as one batch item failure and the transient metric was still counted — so the cost was the log line: written from the branch reserved for defects, with only the message identifier in scope, so an ordinary timeout could not be found by event or order. Case 29 now covers both directions, because the test that existed pinned the defect by asserting a wrapped cancellation throws while passing a token nobody had cancelled. |

## OpenTelemetry (Story 5.3)

| # | Change | Documents | Rationale |
| --- | --- | --- | --- |
| 104 | Gave each record its own span, and the batch none | observability | A batch holds up to ten records, each carrying whatever context its own publisher wrote. A span covering the invocation would have to belong to one of those traces and misattribute the other nine, so what groups an invocation's records is `faas.invocation_id` on every record span rather than a parent that cannot honestly exist. It also rules out `AWSLambdaWrapper.Trace`, which creates exactly that batch-wide span. |
| 105 | Recorded that a deferred record produces no span | observability | It was never attempted. A span would put a zero-length step into the publisher's trace for work the invocation declined to start, which reads as a suspiciously fast success rather than as a deferral. The log event and the metric already report it. |
| 106 | Stated what a missing or malformed `traceparent` does | observability | The specification said context is propagated "where the publisher supports it" without saying what happens where it does not. Both cases produce a root span. Tracing is diagnostic, and failing a record over a telemetry header would turn a monitoring defect into a dead-lettered order. |
| 107 | Fixed the span attribute vocabulary | observability | Two vocabularies, deliberately: OpenTelemetry conventions where they exist, because a backend already knows how to render them, and a `reliable_orders.` prefix everywhere else so nothing collides with a convention added later. Outcome and reason reuse the log vocabulary so a trace and a log line about one record do not describe it in two dialects, and the identifiers are the three the log scope already carries — the Do Not Log list does not stop applying because the destination has different retention. |
| 108 | Set span status from whether the record is returned | observability | Not from whether it succeeded. A duplicate is the idempotency mechanism working, and marking it an error would fill an error view with the outcome this service exists to produce. |
| 109 | Added the collector layer to the Lambda table, pinned, and recorded that the wrapper stays unset | infrastructure | The layer had no row, so nothing said what was deployed. `AWS_LAMBDA_EXEC_WRAPPER` starts the auto-instrumentation the language layers carry, which this service replaces with its own — an assertion now holds it absent, because setting it would run an instrumentation path nothing has been written against. Unlike a container digest, nothing in the build can verify the ARN: a wrong Region, architecture or version fails at deploy. |
| 110 | Recorded the X-Ray write actions as the one unscoped permission | security, infrastructure | The rule was "avoid wildcard resource permissions where service APIs support resource scoping", which permits this without saying it applies. X-Ray defines no resource for either action, so no scoped statement can be written. Naming the exception is what stops it being read as an oversight, and what makes a second one visible. |
| 111 | Derived the required-case count in the plan audit from the document | testing strategy | The audit asserted the coverage table held rows 1 to 30, with the number written into the test and into its name. Adding a case failed it on the count rather than on the missing row, and the cheapest way back to green was to raise the number — which is the opposite of what the audit is for. Both sides are now read from the plan, scoped to the unit-test list because the concurrency and end-to-end sections number their own lists from one. |
| 112 | Generated X-Ray compatible trace identifiers | observability | X-Ray reads the first four bytes of a trace identifier as a Unix timestamp and rejects anything outside roughly a month, so the random W3C identifiers OpenTelemetry generates are refused by the collector's exporter almost every time. The failure is the worst shape available: the function exports, the collector drops, the invocation succeeds, and X-Ray shows nothing. It fixes only the traces this service originates — a record continuing a publisher's trace inherits that publisher's identifier, so the constraint reaches every publisher and is now written down rather than discovered after the first deployment. |
| 113 | Flushed the exporter at the end of every invocation | observability | The batch processor's worker thread lives in the function, not in the collector layer, and Lambda freezes it the moment the handler returns. A record processed near the end of an invocation waited for a thaw that might never come, which on a low-rate queue is most traces rather than an edge case. The earlier remark claiming the collector owned the buffer was true only of spans that had already reached it. Exporting synchronously per span was the alternative and is the wrong trade: six spans a record on a path measured against a deadline. |
| 114 | Marked the extracted parent context as remote | observability | The three-argument `ActivityContext.TryParse` leaves the flag false, so a record continuing a publisher's trace claimed the parent had run in this process. Consumers act on it — the X-Ray translator reads parent remoteness to decide whether a span is the service's entry point or a subsegment of something that never executed here. |
| 115 | Marked the span when processing throws | observability | The outcome is written after the work returns, and that path is the work not returning, so the record that failed for an unexplained reason was the one span missing from a search for errored spans — while the log line and the metric both called it a failure. Cancellation is marked errored with no outcome attribute, because the record reached none and inventing a value outside the fixed vocabulary would put a word in traces that nothing else uses. |
| 116 | Separated the persist and classify spans | observability | The persist span enclosed classification, so its duration included the classifier's and a latency alarm on the write would fire on classifier slowness — on exactly the conflict path where the two most need telling apart. Both documents already claimed the separation; only the code disagreed. |
| 117 | Corrected `messaging.operation` to `messaging.operation.type` | observability | The stated reason for preferring a convention is that a backend already knows how to render it. The name had been retired, so the attribute bought none of that and arrived as something nothing recognises. |
| 118 | Scoped the collector layer to the construct | infrastructure | It was created in the construct's parent, so a second processor construct in one stack collides on the logical identifier at synthesis, and the construct leaked a child into a scope it does not own. |
| 119 | Gave the container ownership of the tracer provider | observability | Registered as an already-built instance, it was never disposed: `Microsoft.Extensions.DependencyInjection` disposes only what it created, so the summary claiming the container held it for the life of the execution environment was wrong about the one thing that matters at the end of that life. What outlived a disposed provider was this process's `ActivityListener`, still attached to the source, and the batch exporter's worker thread, still retrying an endpoint nothing was serving — harmless in a function that runs until the environment is reclaimed, and a leak per provider in a test process. It is now registered behind a factory, whose result the container does dispose, and resolved once by the composition root: nothing else asks for a provider, so without that resolve nothing would be constructed and every span would be dropped by a graph that looked correctly wired. The two halves are one mechanism and each comment names the other. One effect still outlives disposal and is recorded rather than fixed — `AddXRayTraceId` replaces the process-wide `Activity.TraceIdGenerator` and nothing puts the original back. |
| 120 | Made `IncomingMessage` reject a null attribute set | architecture | The type already argued that it, rather than the mapper, is what enforces its shape, because an invariant checked only where SQS records are mapped holds for records that arrived from SQS and for nothing else. Attributes were the one of its four values left unchecked, and the most expensive to leave so: the transport starts the record's span from them before the per-record `try` opens, so a null did not become one record's failure — it left the batch handler as an exception, failed the invocation, and had SQS redeliver every record in the batch including those already committed, which is the replay that handler exists to prevent. Rejected at construction rather than by moving the span inside the `try`, which fixes it for every reader of the attributes and leaves the span where the deadline argument wants it. |
| 121 | Stated the function's architecture and held the collector layer to it | infrastructure | The layer was pinned to `amd64` while the function relied on CDK's x86_64 default, so the remark that the two move together described an intention nothing enforced. The layer is published per architecture and the mismatch is invisible to synthesis: the template is valid, every other assertion passes, and the failure arrives when the collector extension initialises in the deployed environment, where it reads as a broken function rather than as a wrong layer. The architecture is now named once and used on the function, and an assertion reads both sides out of the template — comparing one against a constant would have passed on the only day it is needed. |
| 122 | Corrected two tracing coverage claims | testing strategy | Case 35's duplicate assertion ran through a helper that used a default store, so the outcomes exercised were a processed record and a parse failure; the duplicate the case is named for was never produced, and a change marking duplicates as errors would have passed. The helper now takes the store's result and returns the span, because a status is only meaningful beside the outcome it carries. Case 36 claimed to pin the complete attribute set while running through a span the test started itself, leaving the five attributes the transport writes — at the layer holding the raw SQS record — outside the assertion. Pinned now at both layers, which is what the row says, because no harness runs the real handler and the real processor together. |

## Pull-request CI (Story 7.1)

| # | Change | Documents | Rationale |
| --- | --- | --- | --- |
| 123 | Pinned the CDK CLI in the repository rather than resolving it per run | infrastructure, ci/cd | The CLI is a node package released on its own cadence, and nothing tied it to the `Amazon.CDK.Lib` the constructs come from. Resolved at latest, a release published this morning changes what a pull request synthesises, which is what locked NuGet restore exists to prevent on the other side of the same app. Installing it beside the project puts a `node_modules` inside a project directory, and the CLI ships C# init templates under it: the SDK's default globs compiled `%name.PascalCased%`, so the build failed in files nobody wrote until the project excluded the directory. |
| 124 | Corrected the pull-request CI step list, and stated what synthesis needs | ci/cd | The list described a pipeline that ran the architecture and CDK tests as steps of their own, which no workflow ever did — they are in the one filtered test run, which is what the exclusion filter buys. Synthesis also needed saying: it publishes `--no-build` so the package is the binary the tests ran against, and it sets a placeholder account and Region because the app refuses to synthesise environment-agnostic. Nothing in the stack performs a lookup, so no AWS access is configured for it. |
| 125 | Recorded what the default-branch ruleset requires, and why the summaries exist | ci/cd | The ruleset requires `Build and test` and `lint`, named for the jobs that report them, and a branch behind `main` now has to be updated before it merges — the one failure mode the checks themselves cannot see is a change that passes on its own branch and breaks once merged, which is how `main` came to not compile once already. Neither workflow filters by path, because a required check that never runs reads as outstanding and blocks the merge. The run summary now carries the failures as well as the coverage, because a red run otherwise names the step and leaves the reader to open the log for the test. The TRX logger is left unnamed on purpose: one file name in one directory is one file, and each project would overwrite the last. |

## Container suite diagnostics (Story 6.4)

| # | Change | Documents | Rationale |
| --- | --- | --- | --- |
| 126 | Made the LocalStack fixture report what the container wrote when it will not start | testing strategy | An expired token, a rejected one, and a TLS interceptor the CA bundle does not cover all end in a container that never serves, and the reason is only in its log. Testcontainers reports the exit-55 case with its output attached, so the gap is narrower than it looked when the story was written — what it cannot report is the container that starts, stays up and never answers healthy, where the wait is called off at its ceiling and nothing reads the log before the container is reaped. The fixture now names both variables and prints the last fifty lines of each stream on either path. |
| 127 | Gave the event source mapping stand-in a wait on its retry | testing strategy | It polled with no wait and gave up after two immediate empty responses, so a gather had no slack at all: a send and a receive microseconds apart against an emulator that has not made the message visible yet answers empty twice, and the assertion fails on a short count while naming nothing about the timing that caused it. The first poll stays immediate and every one after it waits a second, which costs a second on an empty queue and two where a partial batch is followed by two empty polls. |
| 128 | Authenticated the integration workflow's image pulls | ci/cd | Both were anonymous against Docker Hub, whose allowance is counted per IP and is therefore shared across every GitHub-hosted runner in the pool rather than being this repository's to spend. The login is conditional on credentials a forked pull request cannot be given, and such a run falls back to the anonymous allowance rather than failing — the trade the LocalStack token already makes. |
