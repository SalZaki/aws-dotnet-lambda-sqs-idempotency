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
- `Outcome`
- `DurationMs`

### Recommended events

- `BatchStarted`
- `MessageParsingFailed`
- `MessageValidationFailed`
- `OrderCreated`
- `DuplicateIgnored`
- `IdempotencyConflict`
- `TransientProcessingFailure`
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
| `TransientFailures` | Count | Retryable record failures |
| `RecordProcessingLatency` | Milliseconds | End-to-end per-record processing duration |
| `BatchSize` | Count | Number of records received |
| `BatchFailures` | Count | Failed records returned in the batch response |
| `DeadlineDeferrals` | Count | Records deferred because invocation time was low |

**Dimensions**, restricted to the following.

- `Service`
- `Environment`

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
