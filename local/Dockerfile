# syntax=docker/dockerfile:1

# The two images the local stack builds: the function, on the base image AWS publishes for the
# managed runtime it is deployed to, and the mapping that stands in front of it.
#
# Both are built here rather than from a publish directory on the host. The CDK deliberately does not
# build the function — the deployment pipeline has already built and tested what it deploys, and
# publishing twice would deploy a second binary that nothing tested — but a local stack has no
# pipeline in front of it, and copying whatever `bin/Release` happens to hold is how a demonstration
# ends up running last week's handler. Docker is already what this stack requires; the SDK image
# makes it the only thing it requires.

ARG SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0
ARG RUNTIME_IMAGE=mcr.microsoft.com/dotnet/runtime:10.0

# The base image for the runtime named in EnvironmentConfig.Development.LambdaRuntimeIdentifier. It
# carries the runtime interface client the handler is loaded by and the runtime interface emulator
# that accepts an invocation over HTTP, which is what lets the deployed artifact be driven locally.
ARG LAMBDA_IMAGE=public.ecr.aws/lambda/dotnet:10

FROM ${SDK_IMAGE} AS build

WORKDIR /source

# The build settings first.
#
# .editorconfig is one of them and not an editor preference. It sets analyzer severities and carries
# the per-file suppressions the repository has argued for, and a build without it fails on rules the
# pull-request gate does not apply.
COPY global.json NuGet.config Directory.Build.props Directory.Packages.props .editorconfig ./

# Then the project files and their lock files, and only those, so that editing a .cs file does not
# invalidate the restore below. Restore is the slow half of this build and nothing in it depends on
# source. Four projects rather than five: the local program references the function, which references
# the AWS adapter and the core, and nothing here builds the publisher, the CDK app or the tests.
#
# A project added without a line here fails the restore that needs it, naming the missing file. That
# is the right direction — the alternative to noticing is a build that silently stops caching.
COPY src/ReliableOrders.Core/*.csproj src/ReliableOrders.Core/packages.lock.json src/ReliableOrders.Core/
COPY src/ReliableOrders.Aws/*.csproj src/ReliableOrders.Aws/packages.lock.json src/ReliableOrders.Aws/
COPY src/ReliableOrders.Function/*.csproj src/ReliableOrders.Function/packages.lock.json src/ReliableOrders.Function/
COPY local/ReliableOrders.Local/*.csproj local/ReliableOrders.Local/packages.lock.json local/ReliableOrders.Local/

# Locked mode, matching the pull-request gate. The committed lock files are the reviewed graph, and
# an image that restored a different one would run something nobody reviewed.
RUN dotnet restore local/ReliableOrders.Local/ReliableOrders.Local.csproj --locked-mode \
 && dotnet restore src/ReliableOrders.Function/ReliableOrders.Function.csproj --locked-mode

# The sources last, which is the layer an ordinary edit invalidates.
COPY src/ src/
COPY local/ local/

RUN dotnet publish src/ReliableOrders.Function -c Release --no-restore -o /published/function \
 && dotnet publish local/ReliableOrders.Local -c Release --no-restore -o /published/mapping

FROM ${LAMBDA_IMAGE} AS function

# LAMBDA_TASK_ROOT is set by the base image and is where the runtime interface client looks for the
# assembly named in the handler.
COPY --from=build /published/function ${LAMBDA_TASK_ROOT}

# The handler, in the Assembly::Type::Method form the runtime resolves. It is the same string
# OrderProcessorConstruct deploys, and LocalStackParityTests holds the two in step — a mismatch here
# would fail on the first invocation rather than at build, which is late enough to be confusing.
CMD ["ReliableOrders.Function::ReliableOrders.Function.Function::HandleAsync"]

FROM ${RUNTIME_IMAGE} AS mapping

WORKDIR /app

COPY --from=build /published/mapping ./

# The verb is supplied by Compose, which runs `provision` to completion and then `run`.
ENTRYPOINT ["dotnet", "ReliableOrders.Local.dll"]
