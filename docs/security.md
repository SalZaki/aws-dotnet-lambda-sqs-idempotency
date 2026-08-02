# Security Requirements

- Use GitHub OIDC; do not store long-lived AWS access keys in GitHub.
- Restrict the OIDC role trust policy to the repository, branch or tag, and GitHub environment.
- Separate CI permissions from deployment permissions.
- Set `contents: read` by default in GitHub Actions.
- Grant `id-token: write` only to deployment jobs that need it.
- Pin third-party GitHub Actions to immutable commit SHAs.
- Apply least-privilege IAM to the following.
  - SQS receive/delete/change-visibility operations
  - DynamoDB item and transaction operations on the two tables
  - CloudWatch logging
  - tracing and telemetry where needed
- Avoid wildcard resource permissions where service APIs support resource scoping.
- Configure a CDK bootstrap permissions boundary for a hardened deployment environment.
- Add `cdk-nag` checks and explicitly document any suppressed finding.
- Enable Dependabot, CodeQL, secret scanning, and dependency review.
- Validate message sizes and field lengths. SQS caps a message at 256 KB; field limits must
  additionally keep the derived DynamoDB item well under 400 KB.
- Do not place the Lambda in a VPC unless required.
- Do not expose the queue publicly.
- Use encryption at rest for queues, tables, and logs where appropriate.
- Keep production data when a stack is deleted; only ephemeral stacks may destroy data.
- Add a threat model covering malformed events, replay, key reuse, resource exhaustion, logging
  leakage, and compromised CI.
