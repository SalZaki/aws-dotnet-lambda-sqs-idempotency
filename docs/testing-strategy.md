# Testing Strategy

## Unit Tests

### Required cases

1. Valid event is parsed.
2. Malformed JSON is rejected.
3. Unsupported schema version is rejected.
4. Validation failures are structured.
5. Non-UTC `occurredAtUtc` is rejected rather than normalised.
6. `occurredAtUtc` outside the skew window is rejected.
7. Canonical hashes match the committed known-answer vectors, so a serializer or runtime change
   that alters canonicalisation fails the build.
8. Events differing only in unknown top-level fields produce identical hashes (see [Contract
   Rules](event-contract.md#contract-rules)).
9. Two events with different `eventId` but identical `data` produce different `EnvelopeSha256` and
   identical `BusinessSha256`.
10. The transaction request body is byte-identical across two attempts at the same event, with
    `TimeProvider` advanced between them (see [Transaction Requests Must Be
    Deterministic](correctness-model.md#transaction-requests-must-be-deterministic)).
11. Valid new order returns `Processed`.
12. Repeated event ID with matching envelope hash returns `Duplicate(Event)`.
13. New event ID, existing order ID, matching business hash returns `Duplicate(Order)`.
14. Reused event ID with a differing envelope hash returns `Conflict(Event)`.
15. New event ID, existing order ID, differing business hash returns `Conflict(Order)`.
16. `IdempotentParameterMismatchException` returns `Conflict(TokenMismatch)` and is not retried.
17. `ConditionalCheckFailed` with a null returned item returns `TransientFailure`.
18. Transient DynamoDB exception returns `TransientFailure`.
19. One failed record produces exactly one batch item failure.
20. Successful records are not included in `BatchItemFailures`.
21. Batch handler returns SQS message IDs rather than event IDs.
22. The failure list never contains null, empty, whitespace, or duplicate identifiers (see
    SqsBatchHandler).
23. `SQSBatchResponse` round-trips through the configured `ILambdaSerializer` and the serialised
    bytes contain the expected `batchItemFailures` entries (see [Composition
    Root](architecture.md#composition-root)).
24. Processing stops safely when the invocation deadline is near and returns `DeadlineDeferred`.
25. Logs do not contain the raw message body.
26. Logs do not contain complete DynamoDB items.
27. Metric dimensions do not contain high-cardinality identifiers.
28. Permanent-failure metrics are suppressed when `ApproximateReceiveCount > 1` (see Retry
    Amplification of Permanent Failures).
29. Cancellation tokens are forwarded, `OperationCanceledException` is not reclassified as transient
    when the token was cancelled, and a client-side timeout is not misclassified as cancellation when
    it was not.
30. Persisted timestamps derive from `occurredAtUtc`, not from `TimeProvider`.
31. A record produces one `Consumer` span carrying the message identifier, the receive count and the
    invocation identifier.
32. A publisher's W3C trace context becomes the record span's parent and is marked remote; an absent
    or malformed one produces a root span rather than a failure.
33. A record deferred at the processing deadline produces no span.
34. Parsing, validation, hashing, persistence and classification are each their own span, and a step
    that never ran produces none.
35. Span outcome and status use the log vocabulary, a duplicate is not an error, and a record whose
    processing throws is still marked errored.
36. Span attributes carry no customer identifier, amount, item description or body.

### Coverage of the required cases

Which test covers each case above, and what the outstanding ones are waiting on. Kept here rather
than in an issue so the plan and its status cannot drift apart.

| Case | Covered by |
| --- | --- |
| 1 | `OrderEventParserTests`, `SampleValidationTests` |
| 2 | `OrderEventParserTests` |
| 3 | `OrderEventParserTests`, `OrderContractSerializerContextTests` |
| 4 | `OrderEventValidatorTests`, `ValidationResultTests` |
| 5 | `OrderEventValidatorTests`, `CanonicalRepresentationTests` |
| 6 | `OrderEventValidatorTests`, `EventSkewWindowTests` |
| 7 | `HashVectorTests` against the committed vectors |
| 8 | `UnknownFieldHashingTests`, and the `unknown-top-level-fields` vector |
| 9 | `CanonicalPayloadHasherTests`, and the `same-data-new-event-id` vector |
| 10 | `OrderTransactionFactoryTests` |
| 11 | `DynamoDbOrderCommandStoreTests`, `OrderCommandStoreTests`, and `OrderMessageProcessorTests` for the `Processed` outcome the store cannot report on its own |
| 12 | `TransactionCancellationClassifierTests`, `OrderClassificationTests` |
| 13 | `TransactionCancellationClassifierTests`, `OrderClassificationTests` |
| 14 | `TransactionCancellationClassifierTests`, `OrderClassificationTests` |
| 15 | `TransactionCancellationClassifierTests`, `OrderClassificationTests` |
| 16 | `DynamoDbOrderCommandStoreTests`, `DynamoDbHarnessTests` |
| 17 | `TransactionCancellationClassifierTests`, over three unusable-item shapes |
| 18 | `DynamoDbOrderCommandStoreTests` |
| 19 | `SqsBatchHandlerTests` |
| 20 | `SqsBatchHandlerTests`, from both sides — a mixed batch and a clean one |
| 21 | `SqsBatchHandlerTests`, over a batch whose records all carry the same event identifier |
| 22 | `BatchItemFailuresTests`, over null, empty, whitespace and duplicate identifiers |
| 23 | `LambdaSerializerTests`, over the bytes the configured serializer writes |
| 24 | `SqsBatchHandlerTests`, asserting the deferred records never reach the processor |
| 25 | `LogRedactionTests`, over every event and over the shape of the log's public surface |
| 26 | `LogRedactionTests`, which also pins the complete field set of a conflict line |
| 27 | `EmbeddedMetricsPublisherTests`, over the dimension set and over the whole published record |
| 28 | `EmbeddedMetricsPublisherTests`, across five deliveries and over the shape of `IInvocationMetrics` |
| 29 | `DynamoDbOrderCommandStoreTests`, over three cases — the token is forwarded, a wrapped cancellation propagates when the token is cancelled, and a wrapped client timeout returns `TransientFault` when it is not. `OrderMessageProcessorTests` covers the same forwarding one layer up |
| 30 | `OrderWriteRequestTests`, `OrderCommandStoreTests` |
| 31 | `TracingTests`, over the whole attribute set of a record span |
| 32 | `TracingTests`, over a propagated context and its remote flag, an absent one, and three malformed headers |
| 33 | `TracingTests`, asserting no span exists for the deferred record |
| 34 | `TracingTests` for parse, validate and hash; `DynamoDbOrderCommandStoreTests` for persist and classify, which the store owns, including that the two are siblings rather than nested |
| 35 | `TracingTests`, over a processed record, a duplicate, the status of an invalid one, and the path where processing throws |
| 36 | `TracingTests`, which pins the attribute set at each layer that writes one rather than checking for known-bad values |

Every case above is covered. Cases 19 to 28 were listed here as outstanding while the components they
describe were still to be written, which is what a plan is for; the column now names a test for each
of them.

### Coverage reporting

The pull-request gate collects line and branch coverage on every run and publishes the Cobertura
report as an artifact. No threshold is enforced. A number picked before the pipeline exists would
either sit below what the suite already reaches, proving nothing, or block work unrelated to the code
that moved it. Publishing the figure on every pull request is what lets a threshold be chosen from
evidence, and every case above is now covered, so the evidence exists and the number can be picked
from it rather than guessed.

## Concurrency Tests

### Required cases

1. Two concurrent calls for the same event produce one creation and one duplicate.
2. Two event IDs for the same order and same business data produce one order and a
   `Duplicate(Order)`.
3. Two event IDs for the same order and different business data produce a `Conflict(Order)`.
4. Transaction cancellation is classified correctly from `CancellationReasons` alone, with no
   follow-up read.
5. A retry after an indeterminate client response remains safe within the `ClientRequestToken`
   window.
6. A retry after the `ClientRequestToken` window has elapsed is still classified as `Duplicate` by
   the conditional writes.

## Integration Tests

Run against containers via Testcontainers.

**Use the official `amazon/dynamodb-local` image for all transaction tests.** The entire
classification path in [DynamoDbOrderCommandStore](architecture.md#dynamodbordercommandstore)
depends on `CancellationReasons[i].Code` being
accurate and on `CancellationReasons[i].Item` being populated when
`ReturnValuesOnConditionCheckFailure` is set. LocalStack's DynamoDB implementation is not dependable
on either point, and a false green here would hide the project's core correctness mechanism. Keep
LocalStack for SQS only.

Both images are pinned to an immutable digest, and `ContainerImageTests` holds each pin in step with
the pre-pull in `integration.yml`.

### SQS Emulation

LocalStack, because Amazon publishes no SQS emulator. What it is trusted for is narrow, and
`SqsHarnessTests` asserts each of those things before anything is built on it: that
`ApproximateReceiveCount` increments across redeliveries, that a redrive policy moves a message to
the dead-letter queue once its receives are spent, and that message attributes survive the round
trip. Nothing about DynamoDB runs against it, and no assertion about transaction cancellation
reaches it.

**LocalStack requires an auth token.** The community and pro images were merged in 2026.3.0, and
since 6 April 2026 the image exits with code 55 before opening its edge port unless
`LOCALSTACK_AUTH_TOKEN` is set. The free tier covers everything these tests do. Set the variable in
your shell to run them locally; CI reads it from a repository secret of the same name. Licence
activation is a network call, so unlike the DynamoDB tests these need outbound network as well as a
Docker daemon.

**Without a token those tests skip rather than fail.** They are marked `[RequiresLocalStack]`, which
skips with a reason naming the variable and this section, so running the whole suite from an IDE on
a machine that was never set up for them reports seventeen passed and eight skipped rather than eight
red. A token that exists but is rejected still fails the container loudly: the condition is whether
one was supplied, not whether it works, because a wrong token is a mistake and an absent one is a
machine that has not been configured.

The workflow declines them a second way, and the two are not redundant. GitHub does not expose
repository secrets to a pull request from a fork, so an outside contributor's run has no token
however the repository is configured. Those tests carry a second `Category` trait,
`RequiresLocalStackToken`, and the integration workflow excludes them and says so when no token is
available. Skipping alone would reach the same verdict, but only after pulling a two-gigabyte image
for tests that were never going to run. The transaction tests are unaffected either way.

**Behind a TLS interceptor, set `LOCALSTACK_CA_BUNDLE` as well.** Activation is an HTTPS call to
`api.localstack.cloud`, and a corporate interceptor — Zscaler, Netskope, and the like — re-signs it
with a certificate the container has no reason to trust. The symptom is exit code 55 and a message
naming a licensing server it cannot reach, which reads as a network outage rather than a trust
problem. Point the variable at the interceptor's root certificate and the fixture mounts it and sets
the three bundle variables the image's outbound clients read. Nothing is committed: the certificate
belongs to the network rather than to this repository, and the variable is unset in CI, where the
runners reach the licensing server directly.

Use the root the vendor publishes rather than the copy already installed on the host. They are not
always the same certificate. Zscaler's locally installed root has been seen without its basic
constraints marked critical, and LocalStack runs Python on OpenSSL 3, which rejects that outright —
so the mount looks ineffective while the error quietly changes from an unknown issuer to a malformed
one. The one published at `mobile.zscaler.net/downloads/zscaler2048_sha256.crt` is correctly formed.

**A container that never becomes ready reports what it wrote.** The fixture wraps the start, reads
the container's output, and fails naming the two variables above with the last fifty lines of each
stream. Testcontainers already reports the exit-55 case with its output attached; what it cannot
report is the container that starts, stays up, and never answers healthy, where the wait is called
off at its ceiling and the reason stays in a log that nothing reads before the container is reaped.

`SSL_NO_VERIFY=1` is LocalStack's own escape hatch and is deliberately set nowhere in this
repository. A committed flag that disables certificate verification is worse than a test that says
it cannot run, and their own documentation limits it to short-term debugging on one machine. The
transaction tests need none of this: `dynamodb-local` makes no outbound call at all.

### The Local End-to-End Path

`BatchResponseTests` runs a published message through the real mapper, handler, processor, and store,
and then acts on the response the way the event source mapping does — deleting every record the
response does not name and leaving the ones it does. That last step is what gives a batch response
consequences. A unit test can assert that the failure list holds one identifier; only a queue can
show that acting on it redelivers one record rather than ten.

The mapping itself is a stand-in, and a partial one: batch window, concurrency, and what happens when
an invocation fails outright are not modelled. Story 6.3 is where the real thing is exercised.

Its gather polls: the first receive is immediate, and every one after it waits a second. Polling
immediately throughout is what a batch assertion cannot survive — a send and a receive microseconds
apart against an emulator that has not made the message visible yet answers empty twice, and the
test fails on a short count rather than on the timing that caused it. Two consecutive empty
responses end the gather, so the wait costs a second on an empty queue — one immediate poll and one
that waits — and two where a partial batch is followed by two empty polls. Both are cheap enough to
leave on the paths that assert a queue is empty.

### Verify

- DynamoDB transaction succeeds for a new order.
- Conditional transaction prevents duplicates.
- `CancellationReasons` carries the conflicting items when the condition fails.
- Both hashes are stored on the correct items.
- TTL attributes are written correctly and derive from `occurredAtUtc`.
- SQS messages can be produced and consumed.
- Batch response mapping is correct.
- Environment configuration is wired correctly.

Local emulation is not the final authority for IAM, Lambda polling, CloudWatch, DLQ movement,
transaction cancellation semantics, or service-specific edge cases.

## CDK Tests

Use CDK assertions rather than relying only on a full-template snapshot.

The suite runs serially. Every CDK call crosses into one node process through jsii, and two test
classes synthesising at once return each other's results — a suite where every class passes alone
fails in bulk when they run together, and then hangs. Test parallelisation is disabled for the
assembly rather than per class, because the constraint belongs to the runtime and applies to whatever
is added next.

### Verify

- Lambda uses the configured .NET 10 managed runtime.
- Event source mapping enables `ReportBatchItemFailures`.
- Queue visibility timeout equals `(6 × lambdaTimeout) + batchWindow + safetyMargin` computed from
  the same `EnvironmentConfig` the construct consumed.
- Event source mapping maximum concurrency is less than or equal to the function's reserved
  concurrency.
- DLQ retention exceeds source retention.
- `maxReceiveCount` is at least 5.
- Queue and table encryption are enabled.
- Lambda timeout, memory, and concurrency are configured.
- X-Ray active tracing is disabled (see [Tracing
  Specification](observability.md#tracing-specification)).
- The ADOT collector layer is attached and pinned to a version.
- `AWS_LAMBDA_EXEC_WRAPPER` is absent, so no auto-instrumentation starts.
- The execution role holds the two X-Ray write actions.
- IAM permissions are resource-scoped.
- Log retention is explicit.
- DynamoDB TTL is enabled on `ExpirationEpochSeconds`.
- Point-in-time recovery and removal policy vary correctly by environment.
- Required dashboard widgets and alarms exist.
- Resource tags exist.

## Real AWS End-to-End Tests

### Required scenarios

1. Send one valid event and verify one order.
2. Send the same event repeatedly and verify one order.
3. Republish the same order under a new event ID and verify one order and no conflict alarm.
4. Send multiple records where only one is invalid and verify successful records are not retried.
5. Send a poison message and verify it reaches the DLQ after the configured receive count.
6. Send an idempotency conflict and verify the metric and alarm path, and that the metric fires once
   rather than once per retry.
7. Verify CloudWatch structured log fields.
8. Verify custom metrics appear.
9. Verify the stack can be destroyed cleanly in an ephemeral environment.

## The Local Development Stack

`compose.yaml` runs the reliability flows by hand, with no AWS account. It is not part of any test
level: Testcontainers starts and disposes what the suite needs from code, and nothing in `dotnet
test` reads this file. It exists so the flows can be watched rather than only asserted, and so the
demonstration in the README has something to demonstrate against. The README carries the commands.

The emulator split is the same one this document argues for above, and for the same reason. SQS is on
LocalStack because Amazon publishes none; DynamoDB is on `amazon/dynamodb-local` because the whole
duplicate-versus-conflict path reads `CancellationReasons`. A stack that served DynamoDB from
LocalStack would demonstrate this project's central claim against an emulator that cannot be trusted
to implement it, which is worse than demonstrating nothing. Both images are pinned to the digests the
fixtures pin, and `ContainerImageTests` now holds three places in step rather than two.

**What is real.** The function runs on `public.ecr.aws/lambda/dotnet:10`, the base image AWS publishes
for the runtime `EnvironmentConfig` deploys to, driven through the runtime interface emulator that
image carries. The handler, the source-generated serializer, the `ILambdaContext` the deadline is
computed from, the DynamoDB transaction and its cancellation reasons are all the deployed ones. The
AWS SDK resolves `AWS_ENDPOINT_URL_DYNAMODB` itself, so the composition root has no local branch in
it and the container runs the same code path an account does.

The invocation timeout is part of that, and it takes a setting to be. The emulator both reports its
own timeout through `RemainingTime` and enforces it, and left alone it picks one several minutes
long — under which `ProcessingDeadline` never reaches its margin and the deadline-deferral path is
unreachable while everything still passes. The stack sets it to the deployed value, and
`LocalStackParityTests` holds the two together.

**What is a stand-in.** The event source mapping, and only that. `ReliableOrders.Local` reads a batch,
invokes the function, and applies the response — deleting every record the response does not name and
leaving the ones it does, which is what gives a batch response consequences. Batch size and the
batching window are modelled, because what a batch is decides what a mixed-batch demonstration can
show — one long poll for the first record, then the deployed `MaximumBatchingWindowInSeconds` to fill
the rest, held in step with the template by `LocalStackParityTests`. Concurrency, scaling, and the
mapping's own failure and retry behaviour are not, as in
[The Local End-to-End Path](#the-local-end-to-end-path). Story
[#32](https://github.com/SalZaki/aws-dotnet-lambda-sqs-idempotency/issues/32) is where the real
mapping is exercised, and nothing here substitutes for it.

**What deliberately differs, and why.** Three things, each because matching production would
demonstrate less rather than more.

| Setting | Deployed | Local | Why |
| --- | --- | --- | --- |
| Visibility timeout | 210 seconds, from the formula | 30 seconds | The stand-in resets a failed record's visibility to zero, so a poison message reaches the dead-letter queue in seconds instead of five timeouts. Redelivery still happens after the same five receives. |
| Event skew window | 24 hours ahead, 5 days behind | 24 hours ahead, 365 days behind | The events in `samples/` carry fixed timestamps because their hashes are what several suites assert on. Rejecting them for age would demonstrate the skew rule rather than the idempotency model. A year is the most `EventSkewWindow` accepts, so the fixtures have to be re-dated once they are older than that — which moves their hashes, and the vectors in `hash-vectors.json` with them. |
| Tracing | OTLP to the ADOT collector | `OTEL_SDK_DISABLED=true` | No collector runs beside the container, and there is no X-Ray to look at. Spans are still started; nothing exports them. |

What does not differ is the part a demonstration is about: the queue names, the redrive setting the
poison-message flow is counted against, the long-poll wait, the table shapes, the handler, and the
service and metric labels on every line the function writes. Those are copies of what the CDK
deploys, because a program that runs in a container cannot reference `Amazon.CDK.Lib` to borrow two
strings and an integer, and `LocalStackParityTests` synthesises the development stack and compares
each of them against the template.

**One flow reads differently on a fast replay, and it is not a defect.** Republishing the byte-identical
duplicate within ten minutes reports `Processed` a second time rather than `Duplicate`, because
`ClientRequestToken` is the event identifier and DynamoDB replays the original transaction's result
inside that window without re-evaluating the conditions. After it, the conditional writes classify it
as `Duplicate(Event)`. Both are correct and both are required cases — 5 and 6 of the concurrency
list — and the claim that holds either way is the one that matters: no second order is written.

## Optional Quality Tests

- Mutation testing for core domain and classification logic
- Architecture tests asserting that `ReliableOrders.Core` references neither `AWSSDK.*` nor
  `Amazon.Lambda.*`
- Load testing with NBomber or k6
- Lambda memory and architecture benchmarks
- Native AOT versus non-AOT cold-start comparison
- Resilience experiments that inject throttling and timeouts
