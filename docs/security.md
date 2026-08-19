# Security Requirements

Design-time controls the implementation must satisfy. Threats, attacker
profiles and mitigations belong in `docs/threat-model.md`, which is not written yet.

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
- Declare the function's execution role rather than accepting the one CDK creates. The default
  attaches the AWS managed `AWSLambdaBasicExecutionRole`, which grants `logs:CreateLogGroup` across
  the account and writes to any log group in it, and which AWS may widen without reference to this
  stack. The declared role is granted writes to this function's own log group and nothing else.
  `logs:CreateLogGroup` is deliberately absent: the group is a resource of the stack, and a function
  that cannot create one cannot start logging somewhere nobody is looking.
- Avoid wildcard resource permissions where service APIs support resource scoping.
  - `xray:PutTraceSegments` and `xray:PutTelemetryRecords` are the exception, and the only one. X-Ray
    defines no resource for either action — the API takes segments rather than an ARN — so there is
    no resource-scoped statement to write. The execution role holds both, because the collector layer
    delivers traces under the function's identity. Nothing else in the stack is unscoped.
- Configure a CDK bootstrap permissions boundary for a hardened deployment environment.
- Run `cdk-nag`'s AWS Solutions rules on every synthesis, registered on the app so that `cdk synth`
  and `cdk deploy` are checked rather than only the test suite. Findings are accepted through the
  CDK's acknowledgement mechanism, declared on the resource they cover and carrying a written reason;
  a test pins the accepted list, so a third acceptance is a deliberate edit rather than a line in a
  construct. Two are accepted today.
  - `AwsSolutions-IAM5[Resource::*]` on the execution role, for the X-Ray actions above.
  - `AwsSolutions-DDB3` on the tables, in environments that turn point-in-time recovery off. An
    environment that retains its data may not: `EnvironmentConfig` refuses that combination, so the
    acceptance cannot cover an environment whose data outlives its stack.
- Enable Dependabot version updates as well as alerts, CodeQL, secret scanning, and dependency
  review. The pinned emulator digests are C# literals that no ecosystem parses, so a scheduled
  workflow compares each against its tag rather than leaving them to decay.
- Require TLS for anything published to the alarm topic. CloudWatch already publishes over HTTPS, so
  what the topic policy removes is a future publisher, or a subscription confirmation, crossing the
  network in the clear.
- Validate message sizes and field lengths. SQS caps a message at 256 KB; field limits must
  additionally keep the derived DynamoDB item well under 400 KB.
- Do not place the Lambda in a VPC unless required.
- Do not expose the queue publicly.
- Use encryption at rest for queues, tables, and logs where appropriate.
- Keep production data when a stack is deleted; only ephemeral stacks may destroy data.
- Add a threat model covering malformed events, replay, key reuse, resource exhaustion, logging
  leakage, and compromised CI.
