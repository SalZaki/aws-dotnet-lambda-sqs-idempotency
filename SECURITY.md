# Security Policy

## What this repository is

A reference implementation of an idempotent SQS worker, published under the MIT Licence. It runs no
hosted service, holds no user data, and nothing here is deployed on anyone's behalf. A vulnerability
found here matters because someone may have copied the pattern into a system that does run.

## Reporting a vulnerability

**Do not open a public issue.**

Use GitHub's private vulnerability reporting, which is enabled on this repository: open the
[Security tab](https://github.com/SalZaki/aws-dotnet-lambda-sqs-idempotency/security) and choose
**Report a vulnerability**. The report stays private between you and the maintainer until a fix is
published.

Please include what you would want to receive yourself.

- What an attacker gains, and what they need to start with.
- The file, resource or workflow the flaw lives in.
- A way to reproduce it — an event that triggers it, a synthesised template, or a failing test.
- Any suggested fix, if you have one in mind.

## What happens next

This is maintained by one person outside working hours, so no response-time guarantee would be
honest. You can expect an acknowledgement that the report was read and understood, a decision on
whether it is in scope, a fix on `main` where it is, and credit in the release notes unless you
would rather not be named.

## Scope

In scope is anything that would compromise a system built on this pattern.

- A path where an event can be processed twice with duplicate business effects, or where a
  legitimate republish is silently lost. The [Correctness Model](docs/correctness-model.md) states
  the guarantees these tests certify.
- Secrets, payloads, or personal data reaching logs, metrics or traces. The
  [Observability](docs/observability.md) document lists what must never be logged.
- IAM, queue or table policies granting more than the workload needs, beyond the single documented
  exception in [Security Requirements](docs/security.md).
- Supply-chain weaknesses: an unpinned action, a dependency confusion route, a workflow that would
  expose credentials to a pull request from a fork.

Out of scope: findings against dependencies that already have a published advisory and a fix — those
arrive as Dependabot pull requests — and reports produced by a scanner without a demonstrated path
through this code.

## What is already automated

Reports that duplicate these are still welcome; knowing what runs may save you time.

| Control | Covers |
| --- | --- |
| CodeQL | C# and workflow analysis, on every pull request |
| Dependabot | Alerts, security updates, and weekly version updates |
| Dependency review | The dependency diff on a pull request, failing at high severity |
| Secret scanning | Including push protection |
| `NuGetAudit` | The whole transitive graph at audit level `low`, failing the restore |
| cdk-nag | AWS Solutions rules on every synthesis of the CDK app |

[Security Requirements](docs/security.md) records the design-time controls, including why exactly one
IAM permission in this stack is unscoped.
