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
11. Security and dependency checks, which run as workflows of their own rather than steps here.
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

Step 9 also runs the cdk-nag rules, because the pack is registered on the app rather than on a test
harness — see [Security Requirements](security.md). A finding nobody has accepted fails the
synthesis, so it fails this gate and any deployment equally.

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

## Dependency Review

The workflow file is `.github/workflows/dependency-review.yml`. It runs on pull requests and fails on
a finding of high severity or above.

NuGet is already gated harder elsewhere: `NuGetAudit` runs at audit level `low` over the whole
transitive graph with warnings as errors, so a .NET package carrying any advisory fails the restore
in the gate above before this workflow has an opinion. What this adds is the two ecosystems restore
never sees — the pinned actions, and the CDK CLI under `infra` — and a diff-scoped report on the
pull request that introduced them. It keeps a read-only token: commenting would need
`pull-requests: write`, and the check turning red is what a reader follows either way.

CodeQL runs from GitHub's default setup rather than a workflow in this repository, over `actions` and
`csharp`, and reports as `Analyze (actions)` and `Analyze (csharp)`.

## Emulator Digests

The workflow file is `.github/workflows/image-digests.yml`. It runs weekly and on demand, resolves
each pinned emulator digest against the tag beside it, and fails naming any that has moved.

The references are read out of the fixtures rather than repeated in the workflow, because a copy
would be a third place to update and the one nobody would remember. Dependabot cannot do this:
`DynamoDbFixture.Image` and `LocalStackFixture.Image` are C# string literals rather than a manifest
any ecosystem parses. It is deliberately not a pull-request check — a tag that moved is news about
the world rather than about the change under review, and failing someone's pull request for it
teaches people to ignore it.

Three outcomes fail it, not one: a digest that has moved, a tag the registry would not resolve, and
a run that found no pinned reference at all. The last is the one worth stating, because a constant
renamed past the workflow's pattern would otherwise report a green tick over nothing checked, which
is the decay this job exists to catch rather than a state it should report as healthy.

## Dependency Updates

Dependabot's configuration is `.github/dependabot.yml`, covering NuGet at the repository root, the
GitHub Actions the workflows pin, and the npm package that pins the CDK CLI. Version updates are
grouped by what a reviewer reads together; security updates are deliberately left ungrouped, so a
CVE fix arrives as its own pull request rather than waiting for whatever else its group is holding.

## Integration Tests

The workflow file is `.github/workflows/integration.yml`. It runs on pull requests, on pushes to
`main`, and on demand.

### Steps

1. Checkout.
2. Install the pinned .NET SDK from `global.json`.
3. Restore using locked dependencies, and build in Release mode.
4. Log in to Docker Hub, when this run has credentials to do it with.
5. Pull `amazon/dynamodb-local` at its pinned digest.
6. Pull `localstack/localstack` at its pinned digest, only when an auth token is available.
7. Run the container-backed tests, as `--filter "Category=Integration"`.

Both images are pre-pulled so that a registry failure is reported as itself rather than as a
container that would not start, and both are pinned to a digest that `ContainerImageTests` holds in
step with the fixtures. No AWS credentials are configured in this job: the tests run against
containers, and an accidental dependency on a real account should surface here as a failure rather
than as a bill.

This workflow is deliberately **not** a required check. These tests are slow, not unimportant, and a
failure here still has to be looked at.

### Docker Hub credentials

Both pulls were anonymous, and the anonymous allowance is counted per IP address, which on a
GitHub-hosted runner is shared with every other job in the pool. The failure that produces is
intermittent, arrives as a pull that cannot be reproduced locally, and gets more likely as the
LocalStack image grows. Step 4 authenticates instead, from `DOCKERHUB_USERNAME` — a repository
variable, because a username is not a secret and hiding it only makes the configuration harder to
check — and a `DOCKERHUB_TOKEN` secret holding a Docker Hub access token with read-only scope.

The step is conditional, and a run without either value pulls anonymously rather than failing. That
is the same trade the auth token below makes: a pull request from a fork can be given neither value
however this repository is configured, and it should do what it can rather than stop at a credential
nobody can hand it.

### The LocalStack auth token

`LOCALSTACK_AUTH_TOKEN` is a repository secret and a licence, not an AWS credential — see [SQS
Emulation](testing-strategy.md#sqs-emulation) for why one is needed at all. It is declared as
job-level `env` rather than on the steps that read it, because a step's own `env` block is not
visible to that step's `if` condition and the `secrets` context is not available there at all. On a
step-level declaration the conditional pull is skipped on every run, including the ones that do have
a token.

GitHub does not expose repository secrets to a pull request from a fork, so an outside contributor's
run has no token however the repository is configured. Step 7 then excludes those tests by trait and
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
