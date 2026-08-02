# .NET Engineering Standards

## Recommended repository-wide settings

```xml
<PropertyGroup>
  <TargetFramework>net10.0</TargetFramework>
  <Nullable>enable</Nullable>
  <ImplicitUsings>enable</ImplicitUsings>
  <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  <AnalysisLevel>latest-recommended</AnalysisLevel>
  <Deterministic>true</Deterministic>
  <ContinuousIntegrationBuild Condition="'$(CI)' == 'true'">true</ContinuousIntegrationBuild>
</PropertyGroup>
```

## Additional standards

- Pin the SDK with `global.json`.
- Use central package management.
- Commit dependency lock files where appropriate.
- Use `DateTimeOffset` for persisted timestamps.
- Inject `TimeProvider` — for latency, deadlines, and skew validation only. Never for values written
  inside the transaction (see [Transaction Requests Must Be
  Deterministic](correctness-model.md#transaction-requests-must-be-deterministic)).
- Forward cancellation tokens.
- Model outcomes as closed record hierarchies with `private protected` constructors; use enums only
  for flat, dimensionless labels.
- Prefer immutable records for contracts and results.
- Use source-generated JSON serialization, and register every type that crosses the Lambda
  serializer boundary — request *and* response (see [Composition
  Root](architecture.md#composition-root)).
- Avoid static mutable state.
- Reuse AWS service clients across invocations.
- Avoid `async void`.
- Avoid sync-over-async.
- Avoid retrying validation and conditional-conflict failures.
- Do not wrap every AWS SDK call in a second generic retry policy.
- Keep exception handling close to the point where a failure can be classified.
- Make logs and metrics part of acceptance criteria, not an afterthought.
