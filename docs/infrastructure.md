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
| Encryption | SQS-managed server-side encryption |
| Message retention | 4 days |
| Visibility timeout | 210 seconds, computed rather than literal (see below) |
| Delivery delay | 0 seconds |
| Receive message wait time | 20 seconds |
| DLQ | `OrdersDeadLetterQueue` |
| `maxReceiveCount` | 5 |
| Resource policy | No public or cross-account send access by default |
| Tags | `Project=ReliableOrdersWorker`, `Environment=<environment>`, `ManagedBy=CDK` |

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
| Encryption | Enabled |
| Message retention | 14 days |
| Redrive allow policy | Restricted to the intended source queue where supported |
| CloudWatch alarm | Visible message count greater than zero |
| Runbook | Inspect, diagnose, repair, redrive, verify |

Do not automatically redrive messages without understanding the failure cause.

### Lambda Function

The logical name is `OrderProcessorFunction`. Initial configuration follows.

| Setting | Value |
| --- | --- |
| Runtime | Managed .NET 10, identifier supplied by `EnvironmentConfig` |
| Package | ZIP |
| Architecture | Configurable, benchmark ARM64 against x86_64 |
| Memory | 512 MB initially |
| Timeout | 30 seconds |
| Ephemeral storage | Default unless benchmark data justifies more |
| Reserved concurrency | Configurable, 10 in development |
| Tracing | OpenTelemetry only, X-Ray active tracing **disabled** |
| Log format | JSON |
| Log retention | Explicitly configured |
| VPC | None unless a real private-network dependency is introduced |
| Execution role | Least privilege |

Environment variables.

```text
ORDERS_TABLE_NAME
IDEMPOTENCY_TABLE_NAME
IDEMPOTENCY_RETENTION_DAYS
POWERTOOLS_SERVICE_NAME       or equivalent service name
ENVIRONMENT
LOG_LEVEL
MAX_EVENT_SKEW_FUTURE_HOURS
MAX_EVENT_SKEW_PAST_DAYS
```

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
| Billing mode | On-demand |
| Encryption | Enabled |
| Point-in-time recovery | Enabled in persistent environments, configurable in disposable development environments |
| Deletion protection | Enabled in production-like environments, disabled in ephemeral tests |
| Removal policy | Retain for production, destroy for ephemeral test stacks |
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

`BusinessSha256` is not optional or diagnostic. The Duplicate and Conflict Classification section
reads it out of the condition-check failure to distinguish a benign republish from a genuine
conflict.

### Idempotency Table

The logical name is `IdempotencyRecordsTable`.

| Setting | Value |
| --- | --- |
| Partition key | `IdempotencyKey` string, the `eventId` verbatim with no prefix or namespace |
| Billing mode | On-demand |
| TTL attribute | `ExpirationEpochSeconds` |
| Encryption | Enabled |
| Point-in-time recovery | Configurable |
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

### CDK outputs

- Source queue URL
- DLQ URL
- Lambda function name
- Orders table name
- Idempotency table name
- Dashboard name

Avoid hard-coded account IDs, Regions, queue URLs, and table names in source code.
