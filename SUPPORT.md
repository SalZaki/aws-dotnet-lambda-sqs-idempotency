# Support

This is a personal reference project, maintained outside working hours. There is no support
contract, no service to be down, and no guaranteed response time. What follows is how to get an
answer with the least waiting.

## Read first

Most questions about behaviour are answered by the design documents, because the behaviour was
argued there before it was written.

| Question | Document |
| --- | --- |
| What does this project guarantee, and what does it deliberately not? | [Overview](docs/overview.md) |
| Why is a duplicate not an error, and why is a republish not a conflict? | [Correctness Model](docs/correctness-model.md) |
| What must a message look like? | [Event Contract](docs/event-contract.md) |
| Which AWS resources exist and how are they configured? | [Infrastructure](docs/infrastructure.md) |
| An alarm fired — what now? | [Runbooks](docs/runbooks/) |
| Why is a test skipped on my machine? | [Testing Strategy](docs/testing-strategy.md#sqs-emulation) |

The [documentation index](docs/README.md) lists all of them and suggests a reading order.

## Then

- **Something is broken**, here or in the documentation: open a
  [bug report](https://github.com/SalZaki/aws-dotnet-lambda-sqs-idempotency/issues/new/choose).
  A failing test or an event that reproduces it is worth more than a description.
- **Something is missing**: open an
  [idea or request](https://github.com/SalZaki/aws-dotnet-lambda-sqs-idempotency/issues/new/choose).
  Say what you were trying to do, since the answer may be that it is a deliberate non-goal — several
  are, and they are listed in the overview.
- **You want to change something**: read [CONTRIBUTING.md](CONTRIBUTING.md).
- **You found a vulnerability**: do not open an issue. Follow [SECURITY.md](SECURITY.md).

Discussions are not enabled. A question worth asking is worth an issue, where the answer stays
findable.

## What is unlikely to get an answer

Help with your own codebase, beyond how this one works. Requests to add a feature that the overview
names as a non-goal, without an argument for changing that. Scanner output with no demonstrated path
through this code.
