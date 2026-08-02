# Delivery

## Open-Source Project Requirements

Before the first public release, add the following.

- Clear project purpose in the first paragraph of the README
- Architecture diagram
- Correctness model explaining at-least-once, idempotency, and the two hash scopes
- Prerequisites
- Local test instructions
- AWS bootstrap instructions
- Deployment instructions
- Sample event commands
- Duplicate demonstration
- Republish demonstration
- DLQ demonstration
- Teardown instructions
- Estimated cost categories and cleanup warnings
- Security model
- Limitations
- Roadmap
- Contribution guide
- Code of conduct
- Security reporting policy
- Support policy
- Licence
- Issue templates
- Pull request template
- Good-first-issue labels
- Architecture decision records
- Demo recording or animated terminal capture

### Repository Naming

The repository already exists as `aws-dotnet-lambda-sqs-idempotency`, and the repository structure
reflects that. It is descriptive and discoverable, leading with the platform and naming the pattern.
Renaming carries a cost — broken links, stale clones, lost stars — that a marginal SEO gain does not
justify.

Keep the current name. Specification v1's suggestions (`dotnet-sqs-idempotent-worker`,
`serverless-dotnet-reliability-lab`) are recorded here only so the decision is visible; if the
repository later hosts several reliability patterns, revisit under an ADR.

## Backlog

The backlog is maintained as GitHub issues, not in this document. A specification
and a plan have different lifecycles. The design here is stable and reviewed
through pull requests, while the work carries state, ownership, and ordering that
markdown cannot represent. Holding both means one of them is always stale.

### Structure

- Epics are issues labelled `epic`, each also carrying `epic-0` through `epic-9`.
- Stories are issues labelled `story`, attached to their epic as a sub-issue and
  carrying the same `epic-N` label plus an area label.
- Milestones M1 through M6 carry the delivery sequence, in the order given by
  their descriptions.
- Epics are deliberately left off milestones. Two of them span several
  milestones, and an epic closes only after its stories do, so assigning them
  would understate milestone progress throughout.
- Ordering constraints are recorded as issue dependencies rather than prose.

### To read the backlog

```bash
gh issue list --label epic
gh issue list --label story --milestone "M1: Correctness First"
gh issue view <n>
```

Two ordering constraints are load-bearing, and both are argued where the design
lives rather than restated here.

- The key and hash decisions in the [Two Idempotency Scopes Require Two
  Hashes](correctness-model.md#two-idempotency-scopes-require-two-hashes) section must be settled
  before any table schema is written, because the [Orders Table](infrastructure.md#orders-table) and
  the [Idempotency Table](infrastructure.md#idempotency-table) persist
  their outcome.
- Transaction classification cannot be verified against an emulator that does not
  report cancellation reasons faithfully (see [Integration
  Tests](testing-strategy.md#integration-tests)).

## Definition of Done

A release is complete when all of the following hold.

- The delivery semantics are described accurately.
- No mark-before-save failure window exists.
- Event-level and order-level idempotency are separately hashed, stored, and tested.
- A republished order under a new event ID is a duplicate, not a conflict.
- Transaction request bodies are deterministic and contain no wall-clock values.
- Classification uses `CancellationReasons` with no follow-up read, and an absent returned item is
  transient.
- New, duplicate, republish, conflict, invalid, and transient scenarios are tested.
- Partial batch response is enabled in both code and infrastructure, and the response survives
  serialization.
- The failure list can never contain a malformed identifier.
- Queue visibility timeout is computed from a documented formula and asserted in CDK tests.
- Permanent-failure metrics are not amplified by retries.
- Local integration tests pass, with transaction tests on `dynamodb-local`.
- Real AWS end-to-end tests pass.
- IAM is least privilege.
- GitHub deployment uses OIDC.
- Logs are structured and free of raw payload and full-item leakage.
- Record-level metrics, dashboard, and alarms exist.
- Exactly one tracing pipeline is active and OpenTelemetry traces are visible.
- The DLQ runbook has been exercised.
- The stack can be deployed and removed using documented commands.
- The README demonstrates valid, duplicate, republish, mixed-batch, and poison-message flows.
- Security and dependency scans contain no unresolved critical findings.
- The repository contains its licence and contribution policies.
- A tagged release is reproducible from the source commit.
