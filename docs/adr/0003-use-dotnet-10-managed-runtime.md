# ADR 0003 — Deploy on the Managed .NET 10 Runtime, Not a Container Image or Native AOT

## Status

Accepted. Implemented by Story 4.3 in
`infra/ReliableOrders.Cdk/Constructs/OrderProcessorConstruct.cs`, with the handler style fixed by
Story 3.3 in `src/ReliableOrders.Function/`.

## Context

Lambda offers three ways to run .NET: the managed runtime with a ZIP package, a container image, and
a custom runtime — which is what Native AOT uses. The choice reaches further than packaging. It
decides the handler's shape, what the cold start costs, what the build has to produce, and which
libraries the project may use at all.

This repository is a reference implementation. Its subject is idempotent SQS batch processing, and
every decision that is not about that competes for a reader's attention with the ones that are.
Native AOT in particular is interesting, well-documented elsewhere, and would put trimming
compatibility in the path of every library choice from here on — including the AWS SDK, the
OpenTelemetry SDK and the ADOT collector's instrumentation.

## Decision

The function is deployed as a ZIP package on the managed .NET 10 runtime, and the handler is a class
library rather than an executable assembly. The managed runtime loads the assembly and the serializer
is supplied by an assembly-level attribute, which keeps the entry point free of bootstrap plumbing.

The runtime identifier is read from `EnvironmentConfig` rather than written into the construct, so
falling back to an earlier managed runtime or forward to something else is a configuration change
rather than a construct edit.

Native AOT is deferred to a benchmark rather than rejected. [Optional Quality
Tests](../testing-strategy.md#optional-quality-tests) records the cold-start comparison as work that
follows the non-AOT implementation, once the selected libraries have been verified for trimming.

## Consequences

The package is the publish output and nothing more: no Dockerfile, no registry, no image scanning,
no digest to pin, and a deployment that carries kilobytes rather than hundreds of megabytes. The
end-to-end run deploys an entire stack in a couple of minutes partly because of this.

Cold start is what the managed runtime gives — worse than Native AOT and better than an unoptimised
container image. This project makes no cold-start claim, and the [Non-Goals](../overview.md#non-goals)
say so.

Library choice stays unconstrained by trimming. That is the consequence worth naming: a reader
following this repository can add a package without first asking whether it survives AOT, which is
exactly the question that would otherwise dominate every later decision.

The handler style is now fixed, and mixing the two is a defect rather than a preference. An
executable assembly using `LambdaBootstrapBuilder` becomes correct only if Native AOT is adopted, and
adopting it means revisiting this record rather than changing one project file.

A managed runtime is a dependency on AWS's release schedule. The .NET 10 identifier has to exist in
the target Region, a CDK assertion pins it, and a Region that does not offer it needs the fallback
the configuration value already allows.

## Alternatives considered

| Alternative | Why it was rejected |
| --- | --- |
| Container image | Buys control over the base image and a size limit measured in gigabytes, neither of which this function needs. It costs a registry, an image to scan and pin, and a slower deploy — and the digest pinning discipline the emulator images already demonstrate elsewhere. |
| Native AOT on a custom runtime | The best cold start, at the price of trimming compatibility for every library from here on, a different handler style, and a build that cannot run on the SDK alone. It is a benchmark this project wants and a foundation it does not. |
| Executable assembly on the managed runtime | Legal, and pointless without AOT: it moves bootstrap plumbing into the entry point in exchange for nothing the managed runtime does not already do. |
| An earlier managed runtime | Would avoid depending on a recent release, and would give up the language and library versions the rest of the implementation is written against. The configuration value makes the fallback available if a Region forces it. |
