# CI/CD Design

Use separate workflows.

## Pull Request CI

The workflow file is `.github/workflows/ci.yml`.

### Steps

1. Checkout.
2. Install the pinned .NET SDK from `global.json`.
3. Restore using locked dependencies.
4. Verify formatting.
5. Build in Release mode.
6. Run unit tests.
7. Run integration tests.
8. Collect test results and coverage.
9. Run architecture tests.
10. Run `cdk synth`.
11. Run CDK assertion tests.
12. Run security and dependency checks.
13. Upload test and coverage artifacts.
14. Never assume a privileged AWS deployment role from an untrusted fork.

## Development Deployment

The workflow file is `.github/workflows/deploy-dev.yml`.

### Trigger options

- push to `main` after CI succeeds; or
- manual workflow dispatch.

### Steps

1. Obtain short-lived AWS credentials through OIDC.
2. Run `cdk synth`.
3. Run `cdk diff`.
4. Deploy the development stack.
5. Execute smoke tests.
6. Publish the stack outputs and test summary.

## Release Deployment

The workflow file is `.github/workflows/release.yml`.

### Trigger

- signed version tag or manual dispatch.

### Requirements

- GitHub protected environment
- optional reviewer approval
- restricted OIDC role
- immutable action SHAs
- deployment concurrency group
- generated release notes
- provenance or artifact attestation where useful

## Ephemeral AWS End-to-End Test

The workflow file is `.github/workflows/e2e.yml`.

### Steps

1. Generate a unique stack name.
2. Deploy an ephemeral AWS stack.
3. Send valid, duplicate, republished, conflicting, and malformed messages.
4. Assert DynamoDB and queue outcomes.
5. Capture logs and metrics on failure.
6. Destroy the stack in an `always()` cleanup step.
7. Use resource tags and a cleanup script to remove orphaned test stacks.
