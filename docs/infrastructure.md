# Infrastructure

## Runtime and Technology Decisions

| Area | Decision |
| --- | --- |
| Runtime | AWS Lambda managed .NET 10 runtime, identifier supplied by configuration (see [Lambda Function](#lambda-function)) |
| Language | C# 14 |
| AWS SDK | AWS SDK for .NET v4 |
| Deployment package | Managed-runtime ZIP package |
| Serialization | `System.Text.Json` with source-generated metadata |
| Queue | Amazon SQS standard queue |
| Persistence | Amazon DynamoDB on-demand tables |
| Atomicity | DynamoDB `TransactWriteItems` |
| IaC | AWS CDK v2 in C# |
| Unit testing | xUnit |
| Test doubles | NSubstitute, Moq, or small hand-written fakes; choose one consistently |
| DynamoDB integration testing | Official `amazon/dynamodb-local` container via Testcontainers |
| SQS integration testing | LocalStack via Testcontainers |
| Authoritative testing | Ephemeral real-AWS end-to-end stack |
| Logging | `Microsoft.Extensions.Logging` or Lambda Powertools structured logging to stdout |
| Metrics | CloudWatch Embedded Metric Format |
| Tracing | OpenTelemetry with the ADOT collector layer and manual instrumentation (see [Tracing Specification](observability.md#tracing-specification)) |
| CI/CD | GitHub Actions with AWS OIDC |
| Security checks | CodeQL, dependency review, Dependabot, `cdk-nag` |
| Licence | MIT |

### Runtime Note

This is a managed-runtime Lambda, not a custom-runtime Lambda. Native AOT can be explored as a
benchmark after the non-AOT implementation is complete and all selected libraries have been verified
for trimming and AOT compatibility.

Confirm that the managed .NET 10 runtime identifier is available in the target Region before the
assertions in the [CDK Tests](testing-strategy.md#cdk-tests) section depend on it. The CDK construct
reads the runtime from `EnvironmentConfig`
(see [AWS CDK Design](#aws-cdk-design)) so that falling back to a container image or an earlier
managed runtime does
not require editing the construct.

## AWS Resource Specification

### Source Queue

The logical name is `OrdersQueue`. Recommended development defaults follow.

| Setting | Value |
| --- | --- |
| Queue type | Standard |
| Queue name | `reliable-orders-<environment>`, explicit rather than generated (see below) |
| Encryption | SQS-managed server-side encryption |
| Message retention | 4 days |
| Visibility timeout | 210 seconds, computed rather than literal (see below) |
| Delivery delay | 0 seconds |
| Receive message wait time | 20 seconds |
| DLQ | `OrdersDeadLetterQueue` |
| `maxReceiveCount` | 5 |
| Resource policy | No public or cross-account send access by default; a TLS-only deny statement on both queues |
| Tags | `Project=ReliableOrdersWorker`, `Environment=<environment>`, `ManagedBy=CDK` |

**Queue names are explicit.** A generated name would be fine for the source queue on its own, and
the reason it is not is the dead-letter queue's redrive allow policy below. That policy has to name
the source queue, and naming it by resource reference makes each queue depend on the other, which
CloudFormation refuses as a circular dependency. With the name fixed, the policy composes the ARN
from the stack's own partition, Region and account and points at the same queue without referencing
the resource. The environment suffix is what keeps two environments in one account apart; because
the names are physical, one account and Region holds one deployment of each environment, and a
second developer wanting their own stack needs a new environment rather than a second copy of `dev`.

The receive message wait time affects only the publisher CLI and any manual `ReceiveMessage` call.
The Lambda event source mapping manages its own polling and ignores the queue setting.

**Visibility timeout formula.** AWS guidance is a visibility timeout of at least six times the
function timeout, plus the maximum batching window. This project adds an explicit operational margin
so that a future timeout increase does not silently invalidate the queue configuration.

```text
visibilityTimeout = (6 × lambdaTimeoutSeconds) + batchWindowSeconds + safetyMarginSeconds
```

Evaluated against the development defaults.

```text
(6 × 30) + 1 + 29 = 210 seconds
```

The `MessagingConstruct` must **compute** this value from `EnvironmentConfig` rather than accept it
as a parameter. Otherwise the assertion in the [CDK Tests](testing-strategy.md#cdk-tests) section
compares a constant against
itself and verifies nothing.

### Dead-Letter Queue

The logical name is `OrdersDeadLetterQueue`.

| Setting | Value |
| --- | --- |
| Queue type | Standard |
| Queue name | `reliable-orders-<environment>-dlq` |
| Encryption | Enabled |
| Message retention | 14 days |
| Redrive allow policy | `byQueue`, naming the source queue by composed ARN |
| Removal policy | Retain where `RetainData`, destroy otherwise |
| CloudWatch alarm | Visible message count greater than zero |
| Runbook | Inspect, diagnose, repair, redrive, verify |

Do not automatically redrive messages without understanding the failure cause.

### Lambda Function

The logical name is `OrderProcessorFunction`. Initial configuration follows.

| Setting | Value |
| --- | --- |
| Runtime | Managed .NET 10, identifier supplied by `EnvironmentConfig` |
| Package | ZIP |
| Architecture | x86_64, the CDK default. Not yet configurable, and benchmarking ARM64 against it is Story 9.3 |
| Memory | 512 MB initially |
| Timeout | 30 seconds |
| Ephemeral storage | Default unless benchmark data justifies more |
| Reserved concurrency | Configurable, 10 in development |
| Tracing | OpenTelemetry only, X-Ray active tracing **disabled** |
| Layers | The ADOT collector, pinned to a version and matching the function's architecture |
| Log format | Text, because the function writes its own JSON (see below) |
| Log retention | 30 days, on a log group the stack declares |
| VPC | None unless a real private-network dependency is introduced |
| Execution role | Least privilege, plus the two X-Ray write actions the collector needs |

The collector layer is pinned to a version for the reason the container images are: an unpinned layer
would change what runs beside the function without a deployment. Unlike an image digest, nothing in
the build can verify the ARN — a wrong Region, architecture or version is rejected at deploy rather
than at synthesis, so the CDK assertion checks the shape and the pin, not the existence.

`AWS_LAMBDA_EXEC_WRAPPER` is deliberately not set. It starts the auto-instrumentation carried by the
language layers, and this function instruments itself; a CDK assertion holds it absent.

Environment variables.

```text
ORDERS_TABLE_NAME
IDEMPOTENCY_TABLE_NAME
IDEMPOTENCY_RETENTION_DAYS
POWERTOOLS_SERVICE_NAME       or equivalent service name
ENVIRONMENT
METRICS_NAMESPACE
LOG_LEVEL
MAX_EVENT_SKEW_FUTURE_HOURS
MAX_EVENT_SKEW_PAST_DAYS
```

The stack sets the five required variables and `IDEMPOTENCY_RETENTION_DAYS`, and deliberately leaves
the rest unset. Restating an optional default in the deployment puts a second copy of it where it
outranks the one the code argues for, and the two then drift.

`ORDERS_TABLE_NAME`, `IDEMPOTENCY_TABLE_NAME`, `POWERTOOLS_SERVICE_NAME`, `ENVIRONMENT` and
`METRICS_NAMESPACE` are required and have no defaults: nothing sensible can be assumed for a table
name, and a defaulted service or environment mislabels every metric in the account. The rest are
optional and fall back to the values their types already carry. A value that is set but unusable
fails the cold start naming the variable rather than falling back, because defaulting over it would
run the service on a number nobody chose while the deployment that set it appeared to take effect.

The log format is Lambda's `Text`, not `JSON`, and that is deliberate. The function writes
structured JSON itself and writes EMF metric records on the same stream. Lambda's JSON format wraps
each stdout line in an envelope of its own, which puts the EMF record's `_aws` key below the root —
CloudWatch extracts metrics only from `_aws` at the root, so every custom metric would stop being
published while the log line carrying it still looked correct.

### Deployment Package

The CDK does not build the function. Synthesis packages whatever
`src/ReliableOrders.Function/bin/Release/net10.0/publish` holds, so `dotnet publish
src/ReliableOrders.Function -c Release` has to run first, and the pipeline that deploys already
builds and tests the solution — publishing again during synthesis would deploy a second binary
that nothing tested.

A publish directory that is missing, or that holds no `ReliableOrders.Function.dll`, fails
synthesis naming the command — CDK would otherwise zip whatever is there and deploy a function
the runtime can find no handler in. The framework in the path is read from the CDK assembly
rather than written down, so a target-framework bump cannot leave synthesis pointing at the
previous framework's directory, which still holds the last build.

Every CDK CLI command synthesises, so `cdk destroy`, `cdk diff` and `cdk ls` need the publish output
too. On a checkout that has not built, run the publish first or point the CLI at an existing cloud
assembly with `cdk destroy --app cdk.out`.

### Event Source Mapping

| Setting | Value |
| --- | --- |
| Source | `OrdersQueue` |
| Batch size | 10 |
| Maximum batching window | 1 second |
| Enabled | True |
| Function response type | `ReportBatchItemFailures` |
| Maximum concurrency | Configurable, 10 initially |
| Event source mapping metrics | Enabled where available |
| Bisect batch | Not applicable to SQS |
| Filtering | Not required for V1 because the queue carries only `order.created` |

Maximum concurrency must be less than or equal to the function's reserved concurrency, so the event
source cannot request more concurrent executions than the function is allowed to use. A CDK
assertion enforces the relationship.

### Orders Table

The logical name is `OrdersTable`.

| Setting | Value |
| --- | --- |
| Partition key | `OrderId` string |
| Table name | Generated by CloudFormation, published as a stack output |
| Billing mode | On-demand |
| Encryption | AWS-managed KMS key |
| Point-in-time recovery | Follows `EnablePointInTimeRecovery` |
| Deletion protection | Follows `RetainData` |
| Removal policy | Retain where `RetainData`, destroy otherwise |
| Secondary index | None in V1 because no query access pattern requires one |

Attributes.

```text
OrderId              partition key
CustomerId
Currency
AmountMinor
ItemDescription
BusinessSha256       hash of the canonical data object, drives classification
EventId              the event that created this order
CorrelationId
SchemaVersion
OccurredAtUtc
CreatedAtUtc         equals OccurredAtUtc, never a wall clock
```

Neither table is named in source. The queues are named because a redrive allow policy has to name
one of them, and no table carries that constraint, so a generated name is kept and published as an
output instead. That leaves a table replaceable without a name collision, and it is what the
function's `ORDERS_TABLE_NAME` and `IDEMPOTENCY_TABLE_NAME` variables are set from.

The execution role is granted `dynamodb:PutItem` on the two table ARNs and nothing else. A
transactional write is authorised by the actions of the items inside it, so `TransactWriteItems`
needs no permission of its own, and the classification path reads the old image out of the
cancellation reason rather than with a follow-up `GetItem` — so no read action is granted at all.

`BusinessSha256` is not optional or diagnostic. The Duplicate and Conflict Classification section
reads it out of the condition-check failure to distinguish a benign republish from a genuine
conflict.

### Idempotency Table

The logical name is `IdempotencyRecordsTable`.

| Setting | Value |
| --- | --- |
| Partition key | `IdempotencyKey` string, the `eventId` verbatim with no prefix or namespace |
| Table name | Generated by CloudFormation, published as a stack output |
| Billing mode | On-demand |
| TTL attribute | `ExpirationEpochSeconds` |
| Encryption | AWS-managed KMS key |
| Point-in-time recovery | Follows `EnablePointInTimeRecovery` |
| Deletion protection and removal policy | Follow `RetainData`, as on the orders table |
| Secondary index | None in V1 |

Attributes.

```text
IdempotencyKey          partition key, equals EventId
OrderId                 the order this event created, for diagnosis and redrive triage
EnvelopeSha256          hash of the canonical envelope, drives classification
OccurredAtUtc
CompletedAtUtc          equals OccurredAtUtc, never a wall clock
ExpirationEpochSeconds  derived from OccurredAtUtc plus retention
```

The `EntityType` and `EntityId` attributes from specification v1 are removed. They implied a
multi-entity keyspace in this table, but the transaction writes exactly one idempotency row per
event and order-level protection comes from the Orders table's own conditional put. Carrying them
would suggest a second row that is never written.

The `Status` attribute is removed for the same reason. A status field exists to distinguish an
in-flight claim from a completed one, which is exactly the mark-then-write design this
specification rejects. Because the idempotency record and the order commit in one transaction, the
only state that can ever be observed is complete, and an attribute with one possible value invites
a reader to assume a second one exists.

`OrderId` is retained deliberately. It is not read by the classification path, but it is what an
operator needs when triaging a DLQ message or an idempotency conflict, and it costs one attribute.

## AWS CDK Design

Use one application stack for the first release, divided into focused constructs.

- `MessagingConstruct`
- `PersistenceConstruct`
- `OrderProcessorConstruct`
- `ObservabilityConstruct`
- `DeploymentIdentityConstruct` only if deployment identity is managed in the same repository

Configuration should be environment-based and typed.

```csharp
public sealed record EnvironmentConfig(
    string EnvironmentName,
    string LambdaRuntimeIdentifier,
    int LambdaMemoryMb,
    int LambdaTimeoutSeconds,
    int ReservedConcurrency,
    int BatchSize,
    int BatchWindowSeconds,
    int MaxConcurrency,
    int VisibilityMarginSeconds,
    int MaxReceiveCount,
    int SourceRetentionDays,
    int DlqRetentionDays,
    int IdempotencyRetentionDays,
    bool RetainData,
    bool EnablePointInTimeRecovery);
```

Derived values — never parameters.

```csharp
public int VisibilityTimeoutSeconds =>
    (6 * LambdaTimeoutSeconds) + BatchWindowSeconds + VisibilityMarginSeconds;
```

The record validates its own invariants on construction. It requires `MaxConcurrency <=
ReservedConcurrency`, `DlqRetentionDays > SourceRetentionDays`, and `MaxReceiveCount >= 5`.

Which configuration a synthesis uses comes from the `environment` CDK context key, defaulted in
`cdk.json` and overridable with `cdk deploy -c environment=<name>`. An unknown name fails synthesis
naming the environments that exist, rather than falling back to the development sizing — deploying
development retention into a production account is not a failure anyone notices on the day.

### CDK outputs

- Source queue URL
- DLQ URL
- Lambda function name
- Orders table name
- Idempotency table name
- Dashboard name

Avoid hard-coded account IDs, Regions, queue URLs, and table names in source code.
