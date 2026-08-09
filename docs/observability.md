# Observability

## Logging Specification

Use JSON structured logging to standard output. Lambda delivers the output to CloudWatch Logs.

Do not make synchronous CloudWatch logging API calls from the handler.

Every record-processing scope should include the following fields.

- `Service`
- `Environment`
- `LambdaRequestId`
- `SqsMessageId`
- `EventId`, when parsed
- `OrderId`, when parsed
- `CorrelationId`, when parsed
- `ApproximateReceiveCount`

`Outcome` and `DurationMs` are fields of a record's terminal event rather than of its scope. A scope
is opened before the work it covers, and neither value exists until that work has finished, so no
ordering makes them available to the lines they would have to precede. Every record reaches exactly
one terminal event, so a query grouping by `Outcome` still sees each record once.

`ProcessingDeadlineReached` is the one terminal event without `DurationMs`. No work was done, so
there is no duration, and a zero would drag down any latency derived from that field at the moment
the handler is under most pressure. It carries `OverrunMs` instead — how far past the deadline the
batch had run, which is what the value can only ever be, since a record is deferred once the deadline
has already passed. The metrics side excludes
deferrals from `RecordProcessingLatency` for the same reason.

Lines are written synchronously, on the thread that logged them. A background writer is the right
trade for a long-running host and the wrong one here: Lambda freezes the execution environment as
soon as the handler returns, so a queued line waits for the next thaw and is lost if the environment
is reclaimed instead. The lines at risk are the last ones written, which is where `BatchCompleted`
lands. Writing synchronously also keeps log lines and EMF records in the order they happened.

Fields are written flat, at the top level of each line, not nested in a `Scopes` array. The order
identity scope opens only after a body parses, so a nested field would sit at a different index on a
parse failure than on a success and one query could not match both.

`EventId` is the publisher's event identifier. The logging framework's own event number is written as
`LogEventId`, with its name in `LogEvent`, because the two are unrelated and sharing a field would
replace a UUID with a small integer on every line that carries both.

Exception messages are not written. Their type and stack trace are, which identifies a defect without
carrying the request bodies and item contents that an SDK exception message holds. A cause an
operator is meant to read belongs in the fixed-vocabulary `Reason` field, and a failure that must be
human-readable, such as missing cold-start configuration, is thrown rather than logged.

### Recommended events

- `BatchStarted`
- `MessageParsingFailed`
- `MessageValidationFailed`
- `OrderCreated`
- `DuplicateIgnored`
- `IdempotencyConflict`
- `TransientProcessingFailure`
- `PermanentProcessingFailure`
- `BatchCompleted`
- `ProcessingDeadlineReached`

### Do not log

- Full SQS bodies
- Customer personal data
- AWS credentials
- Security tokens
- Full exception payloads containing message bodies
- Complete DynamoDB items returned by a condition-check failure — log the compared hashes only

## Metrics Specification

Emit custom metrics asynchronously through CloudWatch Embedded Metric Format.

| Metric | Unit | Meaning |
| --- | --- | --- |
| `OrdersProcessed` | Count | New orders committed |
| `DuplicateEvents` | Count | Duplicate events safely ignored |
| `ValidationFailures` | Count | Permanently invalid events |
| `IdempotencyConflicts` | Count | Key or order ID reused with different data |
| `PermanentFaults` | Count | Requests the store will never accept — a fault in this service |
| `TransientFailures` | Count | Retryable record failures |
| `RecordProcessingLatency` | Milliseconds | End-to-end per-record processing duration |
| `BatchSize` | Count | Number of records received |
| `BatchFailures` | Count | Failed records returned in the batch response |
| `DeadlineDeferrals` | Count | Records deferred because invocation time was low |

**Dimensions**, restricted to the following.

- `Service`
- `Environment`

Metrics are aggregated per invocation and published as one EMF record when the invocation ends, with
each record's latencies carried as an array of values rather than one record per message. Per-record
EMF is what makes Logs ingestion the dominant cost noted below, and CloudWatch derives the same
statistics either way. Publishing happens on disposal so an invocation that throws still reports what
it managed. A batch large enough to exceed EMF's limit of 100 values against one metric publishes the
remaining samples in further records carrying no counters.

A counter that stayed at zero is omitted rather than published as a zero, so that one poison message
produces exactly one data point rather than five, one of which is non-zero. Four metrics are exempt
and are always published. `BatchSize` and `BatchFailures` describe the invocation rather than an
outcome within it, and a continuous failure series is what makes a partial batch failure legible
against a run of successful invocations. `OrdersProcessed` and `DuplicateEvents` are exempt because
alarm 7 is a composite over queue depth and the sum of the two: omitting them when they are zero
would leave that sum with no datapoints during exactly the outage it watches for, so the alarm would
report insufficient data rather than firing. None of the four is gated, so exempting them costs the
first-receipt guarantee nothing.

The CloudWatch namespace is supplied to the publisher by the composition root. It is a deployment
value, not a constant, and every metric above is published under it.

`Outcome` is **not** a dimension. Specification v1 listed it alongside per-outcome metric names,
which counted every record twice under two incompatible query shapes. The discrete metric names are
retained because the alarms in [CloudWatch Dashboard and Alarms](#cloudwatch-dashboard-and-alarms)
are per-outcome; the dimension is
dropped.

Never use `OrderId`, `EventId`, `CustomerId`, or `SqsMessageId` as metric dimensions.

Permanent-failure metrics are gated on `ApproximateReceiveCount == 1` per the Retry Amplification
of Permanent Failures section.

**Cost note.** Per-record EMF to stdout makes CloudWatch Logs ingestion the dominant cost of this
project at any meaningful volume. Record this in `docs/cost-model.md` alongside the fact that
DynamoDB transactional writes consume twice the write capacity of an equivalent unconditional
`PutItem`.

## Tracing Specification

Use OpenTelemetry rather than adding new direct dependencies on the legacy X-Ray SDK.

**Choose one tracing pipeline, not both.** Enabling Lambda X-Ray active tracing alongside an OTel
exporter produces two disconnected trace trees for the same invocation. This project selects OTel.
The Lambda Function section therefore requires active tracing to be disabled, and a CDK assertion
enforces it.

**Realistic scope for .NET.** OTel auto-instrumentation on Lambda is substantially weaker for .NET
than for Node.js, Python, or Java. Plan for the ADOT collector layer plus **manual** OTel SDK wiring
in the composition root. Do not budget this story on the assumption that a layer alone yields useful
spans.

### Approach

- Add the ADOT collector layer and configure the OTLP exporter.
- Use one application-wide `ActivitySource`.
- Instrument DynamoDB AWS SDK calls via the AWS SDK instrumentation package.
- Add spans for parsing, validation, canonical hashing, transactional persistence, and duplicate
  classification.
- Propagate W3C trace context through SQS message attributes where the publisher supports it. The
  event source mapping does not link producer and consumer traces automatically; the link exists
  only because the publisher wrote the context and the handler read it.
- Export to AWS X-Ray and/or CloudWatch Application Signals.
- Keep trace attributes free of sensitive data — no raw bodies, no customer identifiers.

Treat tracing as diagnostic telemetry, not as a source of business correctness.

## CloudWatch Dashboard and Alarms

Create a dashboard in CDK containing the following widgets.

- SQS visible messages
- SQS messages in flight
- Age of oldest source-queue message
- DLQ visible messages
- Lambda invocations
- Lambda errors
- Lambda throttles
- Lambda duration
- Lambda concurrent executions
- DynamoDB consumed capacity
- DynamoDB throttled requests
- Custom processed, duplicate, conflict, and failure metrics
- Per-record latency
- Deadline deferrals

### Required alarms

1. DLQ visible messages greater than zero.
2. Idempotency conflicts greater than zero.
3. Age of oldest source message above an agreed threshold.
4. Lambda throttles above zero for a sustained period.
5. Transient record failures above a threshold.
6. DynamoDB throttling or system errors.
7. No successful processing while messages remain available — a composite alarm over
   `ApproximateNumberOfMessagesVisible` and the sum of `OrdersProcessed` and `DuplicateEvents`.
   The sum matters. A replay storm is processed correctly while `OrdersProcessed` stays flat, so
   alarming on new orders alone would fire on healthy duplicate-only traffic.
8. Deadline deferrals above a threshold, indicating the batch size or deadline margin needs
   adjustment.

Thresholds assume the first-receipt gating described in [Retry Amplification of Permanent
Failures](correctness-model.md#retry-amplification-of-permanent-failures).
If permanent-failure metrics were ever emitted on every attempt, thresholds 2 and 5 would need to
absorb a factor of `maxReceiveCount`.

Every threshold above written as "a threshold" or "an agreed threshold" must carry a concrete value
before the [Definition of Done](delivery.md#definition-of-done) is met. An alarm without a number is
not an alarm.

Partial batch processing can produce successful Lambda invocations that still contain failed
records, so custom record-level failure metrics are mandatory.
