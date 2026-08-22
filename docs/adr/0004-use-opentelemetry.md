# ADR 0004 — Trace with OpenTelemetry, and Disable X-Ray Active Tracing

## Status

Accepted. Implemented by Story 5.3 in `src/ReliableOrders.Aws/Telemetry/` and
`src/ReliableOrders.Core/Observability/Tracing.cs`.

## Context

Two tracing pipelines are available to a .NET Lambda, and the failure mode of choosing badly is that
both work.

Lambda's X-Ray active tracing is a switch on the function: the service produces segments for the
invocation with no code at all. OpenTelemetry is a vendor-neutral SDK with an exporter, reaching
X-Ray here through the ADOT collector layer.

Enabling both produces two disconnected trace trees for the same invocation — one from the service,
one from the exporter — neither of which is wrong and neither of which is complete. An operator
following a slow order finds two half-answers and no way to tell they are halves.

There is a second, quieter problem. OTel auto-instrumentation on Lambda is substantially weaker for
.NET than for Node.js, Python or Java, so a plan that budgets "attach a layer" gets spans that do not
cover the parts this service is about: canonical hashing, the transactional write, and duplicate
classification.

## Decision

OpenTelemetry is the tracing pipeline, and X-Ray active tracing is disabled on the function. A CDK
assertion enforces that it stays disabled, so enabling it is a failing test rather than a discovery
in the console.

The ADOT collector layer is deployed, pinned to a version, with the OTLP exporter configured from the
standard `OTEL_EXPORTER_OTLP_*` variables. `AWS_LAMBDA_EXEC_WRAPPER` is deliberately unset — it
starts the auto-instrumentation this service replaces with its own — and an assertion holds it
absent.

Instrumentation is manual: one application-wide `ActivitySource`, one `Consumer` span per record
parented from that record's own `traceparent`, and no span for the batch. Spans cover parsing,
validation, canonical hashing, transactional persistence and classification. See [Tracing
Specification](../observability.md#tracing-specification).

Tracing is diagnostic. A message with no trace context, or a malformed one, produces a root span
rather than a failure.

## Consequences

There is one trace per record and one pipeline producing it. An operator following a slow order
follows one tree.

The execution role holds `xray:PutTraceSegments` and `xray:PutTelemetryRecords`, which are the only
unscoped permissions in the stack, because X-Ray defines no resource for either action. That
exception is named in [Security Requirements](../security.md) and accepted on the resource it covers
rather than left to look like an oversight.

Nothing in the batch's shape can be read from a parent span, because there is none: a batch holds
records from different publishers and a covering span would have to belong to one of their traces and
misattribute the rest. `faas.invocation_id` on every record span is what groups an invocation
instead.

The spans that exist are the ones someone wrote. That is the cost of manual instrumentation and the
reason the list above is in the specification rather than left to a layer.

Exporting to X-Ray constrains trace identifiers: X-Ray reads the first four bytes as a Unix timestamp
and refuses anything outside roughly a month, so identifiers this service originates are generated to
be compatible. The constraint reaches every publisher whose trace a record continues, which is why it
is written down rather than discovered after a deployment where the function exports, the collector
drops, and X-Ray shows nothing.

Swapping the backend later is a collector configuration change rather than a code change, which is
what choosing the vendor-neutral SDK bought.

## Alternatives considered

| Alternative | Why it was rejected |
| --- | --- |
| X-Ray active tracing alone | Free spans for the invocation, and nothing inside it: no hashing, no transaction, no classification. It would trace the parts nobody asks about and skip the parts this project exists to demonstrate. |
| Both pipelines enabled | Two disconnected trace trees for one invocation. Neither is wrong, neither is complete, and the operator cannot tell. This is the failure the CDK assertion exists to prevent. |
| The X-Ray SDK for .NET, instrumented manually | Comparable effort to OTel, tied to one backend, and a dependency on a legacy SDK for a project whose telemetry is otherwise vendor-neutral. |
| OTel auto-instrumentation through the language layer | `AWS_LAMBDA_EXEC_WRAPPER` and a layer, with spans substantially weaker on .NET than on other runtimes, covering none of the domain steps. It would also run an instrumentation path nothing here has been written against. |
| A span for the batch, parenting the records | A batch's records carry different publishers' trace contexts, so the covering span belongs to one trace and misattributes every other record in the batch. |
