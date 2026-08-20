# Contributing

Thank you for looking. This is a reference implementation as much as a working service, so a change
that improves what it demonstrates is as welcome as one that fixes a defect.

## Before you start

The backlog is [GitHub issues](https://github.com/SalZaki/aws-dotnet-lambda-sqs-idempotency/issues),
organised into epics and milestones. [Delivery](docs/delivery.md#backlog) explains the structure.

Open an issue before a substantial change. Much of this repository is the product of a written
argument — the [Correctness Model](docs/correctness-model.md) and the
[Revision Log](docs/revision-log.md) exist so decisions are not re-argued from scratch — and a pull
request that contradicts one of them needs to address the reasoning rather than only the code. Small
fixes need no issue.

## Building and testing

Prerequisites and the commands are in the [README](README.md#getting-started). In short:

```bash
dotnet restore ReliableOrders.slnx
dotnet build ReliableOrders.slnx -c Release
dotnet test ReliableOrders.slnx -c Release --filter "Category!=Integration"
```

The filter excludes the container-backed tests. Running the whole suite needs Docker, and the SQS
tests additionally need a LocalStack auth token — without one they skip with a reason rather than
fail. [SQS Emulation](docs/testing-strategy.md#sqs-emulation) covers both.

Formatting is part of the build, not a separate step, so a layout violation is a build error. Fix
one with `dotnet format ReliableOrders.slnx`. Markdown is linted too, at 100 columns:
`npx markdownlint-cli2`.

Nothing in the suite reads `compose.yaml`. It is the [local development
stack](README.md#running-it-locally), for watching the flows by hand, and a change to the emulator
images or the queue settings there is held to the fixtures and to the CDK by tests that do run in the
gate — so it fails the build rather than the next person's demonstration.

## What CI enforces

Three checks must pass before a pull request can merge, and the branch must be up to date with
`main` first. [CI/CD Design](docs/ci-cd.md) describes each workflow and why it is shaped as it is.

| Check | What it runs |
| --- | --- |
| `Build and test` | Format, build, every test that needs no container, and a `cdk synth` of the CDK app |
| `lint` | markdownlint over every markdown file |
| `Dependency review` | The dependency diff, failing at high severity or above |

Commits must be signed. The container-backed `Integration tests` workflow is deliberately advisory:
those tests are slow, not unimportant, and a failure there still has to be looked at.

## Commits and branches

Branches are named `type/short-description`, and where a story owns the work,
`type/story-<n>.<n>-description`. The types in use are `feat`, `fix`, `chore`, `docs`, `test`,
`style` and `ci`.

Commits follow [Conventional Commits](https://www.conventionalcommits.org/): a `type(scope):`
subject in the imperative, under about seventy characters, then a body wrapped at 76 columns.

Write the body for the reader who arrives in a year with `git log`. State what changed, and then the
part that is not recoverable from the diff: why the alternative was rejected, what failed before, and
what the change costs. A commit that says only what the diff already shows has thrown away the half
worth keeping.

## Pull requests

The template asks what changed and why. The second half matters more. Where a claim can be measured,
measure it and say so — several decisions in this repository were reversed by measuring them, and
those measurements are recorded in the pull requests that made them.

## Changing dependencies

Package versions are centrally managed in `Directory.Packages.props`, and the resolved graph is
committed as a `packages.lock.json` per project. Restore regenerates them:

```bash
dotnet restore ReliableOrders.slnx --force-evaluate
```

`--force-evaluate` is not optional when a version moves. Each lock file records the version a second
time as a `CentralTransitive` entry, and a restore without it leaves those behind, so CI fails with
`NU1004` rather than anything about the package you changed.

## Changing infrastructure

The CDK app is checked by cdk-nag's AWS Solutions rules on every synthesis, so a new finding fails
the build and the pull-request gate together. Fix it where it is a defect. Where it is a deliberate
trade, accept it through `NagPolicy.Accept` on the resource it covers, with a written reason — and
add the rule to the pinned list in `NagPolicyTests`, so accepting a finding is always a visible edit
rather than a line in a construct nobody reviews.

## Changing documentation

The documents under `docs/` are the design, and the code follows them rather than the other way
around. A change that alters a documented decision belongs in the document as well, with an entry in
the [Revision Log](docs/revision-log.md) naming the defect it addresses. A decision large enough to
be re-argued later belongs in an [architecture decision record](docs/adr/) instead.

## Reporting problems

- A defect: open a
  [bug report](https://github.com/SalZaki/aws-dotnet-lambda-sqs-idempotency/issues/new/choose).
- Something the project should do and does not: open an idea or request. The backlog forms — epic,
  feature, user story and sub-task — are for work that has already been accepted into the plan, and
  [Delivery](docs/delivery.md#structure) explains when each tier is worth using.
- A question or a usage problem: see [SUPPORT.md](SUPPORT.md).
- A vulnerability: **do not open an issue.** Follow [SECURITY.md](SECURITY.md).

## Licence

Contributions are accepted under the [MIT Licence](LICENSE), the same terms as the repository.
