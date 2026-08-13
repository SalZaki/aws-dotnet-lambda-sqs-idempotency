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
10. Publish the function with `dotnet publish src/ReliableOrders.Function -c Release`.
11. Run `cdk synth`.
12. Run CDK assertion tests.
13. Run security and dependency checks.
14. Upload test and coverage artifacts.
15. Never assume a privileged AWS deployment role from an untrusted fork.

Synthesis packages the publish output rather than building it, so step 10 is not optional — without
it `cdk synth` fails naming the command. See [Deployment
Package](infrastructure.md#deployment-package).

## Development Deployment

The workflow file is `.github/workflows/deploy-dev.yml`.

### Trigger options

- push to `main` after CI succeeds; or
- manual workflow dispatch.

### Steps

1. Obtain short-lived AWS credentials through OIDC.
2. Publish the function with `dotnet publish src/ReliableOrders.Function -c Release`.
3. Run `cdk synth`.
4. Run `cdk diff`.
5. Deploy the development stack.
6. Execute smoke tests.
7. Publish the stack outputs and test summary.

The deployed artefact is the one this job published. Deploying a build from an earlier job means
carrying the publish output between them as an artifact, which is a decision for the workflow that
implements this rather than a second `dotnet publish` here.

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
2. Publish the function with `dotnet publish src/ReliableOrders.Function -c Release`.
3. Deploy an ephemeral AWS stack.
4. Send valid, duplicate, republished, conflicting, and malformed messages.
5. Assert DynamoDB and queue outcomes.
6. Capture logs and metrics on failure.
7. Destroy the stack in an `always()` cleanup step.
8. Use resource tags and a cleanup script to remove orphaned test stacks.

The cleanup step needs the publish output as much as the deploy does, because `cdk destroy`
synthesises before it deletes anything. A job that lost its workspace, or one re-run from a clean
checkout, has to publish again or pass `--app cdk.out` — otherwise the teardown fails at synthesis
and leaves the stack it was there to remove.
