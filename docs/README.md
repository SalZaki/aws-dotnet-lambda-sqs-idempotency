# Documentation

Design documentation for the reliable SQS worker. The project front door is the
[repository README](../README.md).

Specification version 3. Design complete, backlog created, implementation not started. Change
history is in the [Revision Log](revision-log.md).

## Contents

| Document | Covers |
| --- | --- |
| [Overview](overview.md) | What this project is, why it exists, what it deliberately excludes. |
| [Correctness Model](correctness-model.md) | Delivery semantics, the two hash scopes, transaction determinism, and how every failure is classified. |
| [Event Contract](event-contract.md) | The versioned envelope, its validation rules, and canonical hashing. |
| [Architecture](architecture.md) | Component diagram, application components and their contracts, and the repository layout. |
| [Infrastructure](infrastructure.md) | Runtime and technology decisions, every AWS resource, and the CDK design that creates them. |
| [Observability](observability.md) | Structured logging, metrics, tracing, dashboard and alarms. |
| [Security Requirements](security.md) | OIDC, least-privilege IAM, supply-chain controls and data-handling rules. |
| [CI/CD Design](ci-cd.md) | Pull-request CI, development and release deployment, and the ephemeral end-to-end workflow. |
| [Testing Strategy](testing-strategy.md) | Unit, concurrency, integration, CDK and real-AWS tests, and which emulator is trustworthy for what. |
| [.NET Engineering Standards](engineering-standards.md) | Repository-wide build settings and the coding rules that protect the correctness model. |
| [Delivery](delivery.md) | Open-source requirements, where the backlog lives, and the definition of done. |
| [Revision Log](revision-log.md) | Every change from specification v1 onward, each with the defect it addresses. |

## Reading order

Read [Overview](overview.md) first, then [Correctness Model](correctness-model.md). Those two
carry the reasoning every other document depends on. The rest can be read in any order.

The backlog is not in these documents. It is maintained as GitHub issues, for the reasons given
in [Delivery](delivery.md#backlog).
