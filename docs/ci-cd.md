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

A NuGet bump needs a commit on top of what Dependabot proposes, and this is expected rather than a
misconfiguration. Central package management puts the version in `Directory.Packages.props` while
each project's lock file records it again as a `CentralTransitive` entry, and Dependabot updates the
first without the second — so locked-mode restore refuses the result with `NU1004`. Regenerate with
`dotnet restore ReliableOrders.slnx --force-evaluate` and commit the lock files with the bump.

Groups are matched independently rather than first-fit, so a package can land in two of them at
once. `Microsoft.Extensions.TimeProvider.Testing` did on the first run, appearing in the testing and
the microsoft-extensions pull requests together; the second group excludes it by name.

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

## Deployment Identity

Two things decide who may deploy, and neither is sufficient alone.

IAM decides which GitHub environment may assume which role. The trust policy of each role in
`DeploymentIdentityStack` demands two claims by `StringEquals`: the audience, so a token GitHub
minted for another service cannot be replayed here, and the subject
`repo:<owner>/<repo>:environment:<name>`, which a job carries only when it names that environment.
See [Deployment identity](infrastructure.md#deployment-identity) for the stack and what the roles may
do once they are assumed.

GitHub decides which ref may reach an environment, and only GitHub can. A trust policy naming an
environment nobody restricted is a trust policy naming every branch, so the environments carry the
other half:

| Environment | Admits | Reviewer | Assumed by |
| --- | --- | --- | --- |
| `dev` | branch `main` | none | `deploy-dev.yml` |
| `release` | tag `v*` | the repository owner | `release.yml` |

`scripts/configure-deployment-environments.sh` creates both, writes those policies, and stores
each role ARN as an environment secret. It is a script rather than a page of instructions because the
policies are the control, and a control nobody can re-apply is one nobody can check. The ARN is a
secret rather than a variable only because it carries the account ID and this repository is public;
it is not a credential, and assuming the role still needs a token GitHub mints for a job in that
environment.

No AWS access key is stored anywhere in this repository's configuration, and none ever was. The only
secrets it holds are a Docker Hub token and the LocalStack licence, both of which the integration
workflow reads and neither of which reaches an AWS account. Every credential a deployment uses is
minted for the job that uses it and expires with it.

Setup, in the order it has to happen:

1. Bootstrap the account, once, with credentials nobody stores:
   `npx cdk bootstrap aws://<account>/<region>`.
2. Deploy `ReliableOrders-DeploymentIdentity` and read the two role ARNs from its outputs.
3. Run the script with those ARNs and the Region.

### No credentials from a fork

The requirement carried from #33 is that no workflow reachable from a forked pull request can assume
the deployment role. Three things hold it, and the third is the one that matters.

Neither deployment workflow triggers on `pull_request`. Both jobs name an environment, and GitHub
does not expose an environment's secrets to a run that is not deploying to it.

The third is `deploy-dev.yml`'s own condition. It is triggered by a completed run of `ci`, and `ci`
runs on pull requests — so a fork's pull request does raise this workflow, in this repository's
context, with this repository's secrets. That is the documented behaviour of `workflow_run` rather
than a misconfiguration, and it is why the job requires the triggering run to have succeeded, to have
been a push, to have been a push to `main`, and to have come from this repository. Only the last is
outside a fork's control.

A test reads both files back: no `pull_request` anywhere in either, an environment named in each
deploying job, and that comparison present in the condition.

## Development Deployment

The workflow file is `.github/workflows/deploy-dev.yml`.

### Trigger options

- a completed successful run of `ci` on a push to `main`; or
- manual workflow dispatch.

It runs after `ci` rather than beside it. A deployment that raced the gate would put an untested
commit into the account roughly as often as the gate is slower than this workflow. What it checks out
is `github.event.workflow_run.head_sha` — the commit the gate ran against — because a `workflow_run`
event checks out the default branch by default, and a push that landed while `ci` was running would
otherwise deploy a commit no gate has seen.

No deploying job starts until `AWS_REGION` is set, which is the variable the setup script writes and
the one no deployment can work without. It stands in for "this repository has an account to deploy
to": without the clause, a checkout with nothing configured runs as far as the credentials step and
fails there, which is what this repository did on the first push after the deployment story merged
and what every fork of it would do on every push. Referencing an environment also creates it, so that
first run left a `dev` environment behind with no branch policy on it.

The variable rather than the role ARN it implies, because `vars` is readable in a job condition and
`secrets` is not. It has to stay a **repository** variable: `vars` in a job condition carries
repository and organisation variables only, since the environment is resolved after the condition is
read. Moved onto the `dev` environment beside the role ARN, where it looks like it belongs, it reads
as empty there and as its value everywhere else — so both workflows would stop deploying for good
while every step still resolved it.

The cost is that a variable someone deletes stops deployments quietly rather than loudly. Three
things carry that weight instead: the setup script says so when `--region` is absent and nothing is
set, the environment's activity log records no deployment for a push that deployed nothing, and the
guard is on the deploying job alone rather than the workflow — so what is skipped is visible in the
run beside the jobs that were not.

`ci` alone, not the integration workflow. Both run on a push to `main` and `ci` is the faster, so a
push whose container-backed tests then fail has already deployed. That follows from the same decision
that leaves those tests off the required checks — they are slow, they are not unimportant, and a
failure in them is looked at rather than gating. Waiting on both would mean deploying twice or
building a join between two `workflow_run` events, which is machinery for a development environment
that a later push replaces anyway. Stated here because "after the gate" would otherwise imply more
than it means.

### Steps

1. Obtain short-lived AWS credentials through OIDC.
2. Publish the function with `dotnet publish src/ReliableOrders.Function -c Release`.
3. Run `cdk diff`.
4. Deploy `ReliableOrders-dev`, writing the stack outputs to a file.
5. Check the outputs and summarise them.

The deployed artefact is the one this job published. Downloading what `ci` published would mean
trusting an artifact named by a run this job did not watch, and the publish costs less than the
download would. There is no separate `cdk synth`: the deploy synthesises, and a template that fails
to synthesise has already failed the gate.

The outputs are the deployment's own account of what it made, and a missing one means the stack
deployed without something the runbooks and the end-to-end tests reach for by name — which
CloudFormation reports as success. The check is `scripts/check-stack-outputs.py` rather than a step,
because the release deployment runs the same one and a second copy of the six names would be the one
that fell behind. A test holds that list in step with the stack's outputs, and a second reads both
workflows for the call, so neither an output renamed in one place nor a deployment that quietly
stopped checking passes unnoticed.

Names are published to the run summary, never values. A queue URL carries the account ID, a step
summary on a public repository is public, and the account-ID masking that covers the log does not
cover that file. The masking is asked for explicitly — `configure-aws-credentials` does not mask by
default.

## Release Deployment

The workflow file is `.github/workflows/release.yml`. It deploys a signed tag, in three jobs, and
what distinguishes it from the workflow above is what has to happen before it rather than what it
deploys.

### Trigger

- a `v*` tag, or a manual dispatch aimed at one. A dispatch aimed at a branch is refused: releases
  are cut from tags, and a branch reaching the deployment would deploy whatever it points at under a
  release's name.

### Jobs

1. `verify` reads the tag through the API and fails unless GitHub reports it as a verified signature.
   A lightweight tag is refused as carrying nothing to verify. This runs before the approval, so a
   reviewer is never asked to approve a deployment of something the repository cannot show came from
   a key it knows. It emits the commit the tag pointed at.
2. `deploy` runs in the `release` environment, waits for its reviewer, checks out that commit, and
   deploys under the release role. The commit rather than the tag, because the approval gate sits
   between the two jobs and a tag is a movable reference — resolving it again on the far side would
   verify one commit and deploy whichever the tag named by then. It checks the stack outputs the way
   the development deployment does.
3. `publish` creates the release with generated notes, and does nothing if the release already
   exists, so a re-run after a transient failure does not report red over a deployment that
   succeeded. It is a job of its own because it is the only one here that writes to the repository,
   and the job holding AWS credentials has no business also holding a token that can publish.

Only the second of those asks whether there is an account. A release is a fact about this repository
and a deployment is one about an account, so a repository with nothing configured still verifies its
tag and still publishes its release, and the deployment is what is skipped. The publication does not
survive a deployment that ran and failed, which is a release nobody should be reading notes for.

Both deployment workflows share the concurrency group `deploy-reliable-orders-dev`, because they
deploy the same stack. Neither cancels a run in flight: a cancelled deployment leaves the stack
mid-update, which is a state an operator has to clear by hand.

The group is declared on the deploying job rather than on the workflow, and that placement is
load-bearing. A workflow-level group is entered by the run before any job's condition is read, and
every completed run of `ci` raises `deploy-dev` — pull requests included, where the job is then
skipped. GitHub keeps one pending run per group and cancels the run it replaces, so a no-op raised by
an unrelated pull request could cancel a queued deployment of `main` while a release held the group.
A skipped job never enters.

`dev` is the only environment defined today, so a release deploys what a push to `main` deploys, and
the release role and the development role differ in trust rather than in reach. That is worth stating
plainly rather than dressing up. What the release adds is the verified tag, the reviewer, and the
notes; what the separate role buys is that CloudTrail can tell a release apart from a push, and that
a second account can be trusted separately the day [Story 9.4](../docs/delivery.md#backlog) defines
one — without re-trusting anything that exists.

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
