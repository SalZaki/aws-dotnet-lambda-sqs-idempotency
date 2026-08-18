# CI/CD Design

Use separate workflows.

## Pull Request CI

The workflow file is `.github/workflows/ci.yml`.

### Steps

1. Checkout.
2. Install the pinned .NET SDK from `global.json`, and Node.
3. Restore using locked dependencies.
4. Verify formatting.
5. Build in Release mode.
6. Run every test that needs no container, as `--filter "Category!=Integration"`, collecting TRX
   reports and coverage. The architecture and CDK assertion tests are in that run rather than in
   steps of their own, which is what the exclusion filter buys.
7. Summarise the results and the coverage in the run summary.
8. Publish the function with `dotnet publish src/ReliableOrders.Function -c Release --no-build`.
9. Install the pinned CDK CLI and run `cdk synth`.
10. Upload the TRX reports and the coverage report.
11. Run security and dependency checks. Owned by #35, and not present yet.
12. Never assume a privileged AWS deployment role from an untrusted fork.

Container-backed tests are **not** in this gate. They pull large images and start containers, which
is minutes against a gate that otherwise finishes in well under one, and they run in the workflow
below instead. The filter is written as an exclusion rather than an inclusion so that a new test
project nobody remembers to wire up still runs here, which is the safe direction to be wrong in.

Synthesis packages the publish output rather than building it, so step 8 is not optional — without
it `cdk synth` fails naming the command. It publishes `--no-build`, so what is packaged is the
binary step 6 ran against rather than a second one nothing has exercised. See [Deployment
Package](infrastructure.md#deployment-package).

Synthesis needs no AWS access. `CDK_DEFAULT_ACCOUNT` and `CDK_DEFAULT_REGION` are set to a
placeholder account and a Region because the app demands them — see [AWS CDK
Design](infrastructure.md#aws-cdk-design) for why an environment-agnostic stack is refused — and
nothing in this stack performs a context lookup, so the placeholder synthesises the template a real
account would. No credentials are configured in this job.

The CDK assertion tests build the stack in process, which catches a construct that throws. Step 9
catches what they cannot: a `cdk.json` the app can no longer be run through, and a CLI that has
moved past the library. A template that no longer synthesises then fails the pull request rather
than the deployment.

### Reporting

Both summaries are written to the run summary rather than left inside an artifact. A failed run
otherwise reports which step failed and leaves the reader to open the log for which test, and a
coverage number nobody reads cannot inform the threshold decision it exists to inform.

The test summary counts each suite, then shows the first twenty failures with their message and
stack trace. The reports themselves are uploaded as well, because a run with more failures than
that is exactly the run whose detail is worth reading in full.

The TRX logger is configured without a `LogFileName`. One name in one results directory is one file,
and each test project would overwrite the last, leaving a report for whichever assembly finished
last and no trace of the rest.

Coverage is collected and published, not enforced. A threshold is a number the team has to agree,
and one picked here would either sit below what the suite already reaches, which proves nothing, or
block work unrelated to the code that moved it.

### Required checks

The repository's default-branch ruleset requires two checks, named for the jobs that report them:
`Build and test` from this workflow, and `lint` from `markdownlint.yml`. It also requires a pull
request and signed commits, and refuses deletion and non-fast-forward pushes on `main`. The
integration workflow is deliberately not required, for the reason given below.

A required check has to run on every pull request, which is why neither workflow filters by path. A
gate that skips on some changes reports "not run" rather than "passed", GitHub reads a check that
never ran as outstanding, and the pull request can then never merge. That is not hypothetical: it
blocked the fix for a `main` that did not compile.

A branch has to be up to date with `main` before it merges. The cost is a rebase on every merge that
lands behind another; what it buys is catching a change that passes on its own branch and fails once
merged, which no other check in this list can see — and that is how `main` came to not compile once
already.

## Integration Tests

The workflow file is `.github/workflows/integration.yml`. It runs on pull requests, on pushes to
`main`, and on demand.

### Steps

1. Checkout.
2. Install the pinned .NET SDK from `global.json`.
3. Restore using locked dependencies, and build in Release mode.
4. Pull `amazon/dynamodb-local` at its pinned digest.
5. Pull `localstack/localstack` at its pinned digest, only when an auth token is available.
6. Run the container-backed tests, as `--filter "Category=Integration"`.

Both images are pre-pulled so that a registry failure is reported as itself rather than as a
container that would not start, and both are pinned to a digest that `ContainerImageTests` holds in
step with the fixtures. No AWS credentials are configured in this job: the tests run against
containers, and an accidental dependency on a real account should surface here as a failure rather
than as a bill.

This workflow is deliberately **not** a required check. These tests are slow, not unimportant, and a
failure here still has to be looked at.

### The LocalStack auth token

`LOCALSTACK_AUTH_TOKEN` is a repository secret and a licence, not an AWS credential — see [SQS
Emulation](testing-strategy.md#sqs-emulation) for why one is needed at all. It is declared as
job-level `env` rather than on the steps that read it, because a step's own `env` block is not
visible to that step's `if` condition and the `secrets` context is not available there at all. On a
step-level declaration the conditional pull is skipped on every run, including the ones that do have
a token.

GitHub does not expose repository secrets to a pull request from a fork, so an outside contributor's
run has no token however the repository is configured. Step 6 then excludes those tests by trait and
says so in a warning, and the rest still run. They would skip themselves in any case, but only after
paying for a two-gigabyte pull.

The job carries a timeout. A licence that cannot activate never satisfies either container wait
condition, and the Testcontainers default would spend an hour reaching that conclusion.

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
