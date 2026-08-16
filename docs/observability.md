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

- The ADOT collector layer, pinned to a version, with the OTLP exporter configured from the standard
  `OTEL_EXPORTER_OTLP_*` variables. The collector alone, not a language layer:
  `AWS_LAMBDA_EXEC_WRAPPER` is deliberately unset, because it starts the auto-instrumentation this
  service replaces with its own.
- One application-wide `ActivitySource`, named for the assembly and defined in `Tracing`.
- DynamoDB SDK calls instrumented through the AWS SDK instrumentation package.
- Spans for parsing, validation, canonical hashing, transactional persistence, and duplicate
  classification.
- W3C trace context read from SQS message attributes. The event source mapping does not link
  producer and consumer traces; the link exists only because the publisher wrote the context and the
  handler read it. A message with none, or with a malformed header, produces a root span rather than
  a failure — tracing is diagnostic, and refusing an order over a telemetry field would turn a
  monitoring defect into a dead-lettered message.
- Export to AWS X-Ray through the collector. The execution role therefore holds the two X-Ray write
  actions, which are the only unscoped permissions in the stack; see [Security
  Requirements](security.md).
- Keep trace attributes free of sensitive data — no raw bodies, no customer identifiers.

Treat tracing as diagnostic telemetry, not as a source of business correctness.

#### One span per record, and no span for the batch

A batch holds up to ten records, each carrying whatever trace context its own publisher wrote. A span
covering the invocation would have to belong to one of those traces and would misattribute the rest,
so there is none: each record is a `Consumer` span parented from its own `traceparent`, and every
record span carries `faas.invocation_id`. That attribute is what groups an invocation's records, in
place of a parent that cannot exist.

A record deferred at the processing deadline produces no span at all. It was never attempted, and a
span would put a zero-length step into the publisher's trace for work this invocation declined to
start. Deferral is reported by its log event and its metric.

#### X-Ray decides the shape of a trace identifier

X-Ray reads the first four bytes of a trace identifier as a Unix timestamp and rejects anything
outside roughly a month. OpenTelemetry generates random W3C identifiers, so without intervention the
collector's exporter drops almost every span — and drops them silently, with the function exporting,
the invocation succeeding and X-Ray showing nothing. The tracer provider therefore generates X-Ray
compatible identifiers.

That fixes the traces this service originates. **It does not fix a trace it continues.** A record
parented from a publisher's `traceparent` inherits the publisher's identifier, so a publisher that
generates ordinary W3C identifiers produces traces the exporter refuses — the propagated traces,
which are the ones propagation exists for. Either every publisher generates X-Ray compatible
identifiers, or
the collector is configured to skip timestamp validation, or the export goes somewhere that does not
impose the format. The demonstration publisher in Story 8.2 has to make the same choice.

#### The exporter batches inside this process

Spans are queued in the function and flushed at the end of every invocation, not left to the
exporter's schedule. Lambda freezes the execution environment as soon as the handler returns, which
stops the batch worker mid-queue: a record processed near the end of an invocation would wait for a
thaw that may never come, and on a low-rate queue that is most traces. The flush is bounded so a
collector that is not listening cannot hold an invocation open.

Exporting synchronously per span would avoid the queue and is the wrong trade — six spans a record,
ten records an invocation, each paying a round trip on a path measured against a deadline.

#### Attribute vocabulary

Where OpenTelemetry defines a convention, the convention wins — `messaging.system`,
`messaging.message.id`, `messaging.operation.type` and `faas.invocation_id` — because a backend already
knows how to render them. Everything else is prefixed `reliable_orders.` so it cannot collide with a
convention added later.

The outcome and reason attributes use the same vocabularies as the log fields of the same names, so
a trace and a log line about one record agree rather than describing it in two dialects. Span status
is set from whether the record is being returned, not from whether it succeeded: a duplicate is the
idempotency mechanism working and is not an error.

The identifiers on a span are the three the log scope carries — event, order and correlation. No
customer identifier, no amount, no item description, no body. The Do Not Log list does not stop
applying because the destination is a different system with different retention.

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
