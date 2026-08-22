# ADR 0006 — Set InvariantGlobalization Repository-Wide

## Status

Accepted. Implemented in `Directory.Build.props`, with the validation rules it constrains in
`src/ReliableOrders.Core/Validation/OrderEventValidator.cs` from Story 1.2.

## Context

The event's canonical representation is hashed, and the two hashes decide whether a redelivery is a
duplicate, a republish or a conflict. Anything that makes the same event parse or compare differently
on two machines therefore reaches the correctness model, not just the output.

Culture is exactly that kind of anything. `string.Compare`, `ToUpper`, number and date parsing, and
`RegionInfo` all consult culture data by default, and what that data says depends on the ICU version
present. A developer machine, a container image and the Lambda managed runtime need not agree — and
the disagreement surfaces as a hash that differs between environments, which reads as a corrupted
event rather than as a globalization setting.

The setting is also not free to place. Applying it to the function project alone would leave the test
suites running under a different globalization mode from the code they certify, so a culture-sensitive
comparison would pass every test and fail in the account.

## Decision

`InvariantGlobalization` is `true` in `Directory.Build.props`, so it applies to every project in the
repository — the function, the CDK app, the local stack and all five test suites alike. Tests observe
what the Lambda observes.

Validation is written so that no rule depends on culture data. Currency is checked as three ASCII
upper-case letters rather than against `RegionInfo`, and every comparison that decides an outcome is
`StringComparison.Ordinal`.

## Consequences

The same event hashes identically wherever it is processed, and the reason it does is a build property
rather than a convention nobody can enforce at review time.

The managed runtime skips loading ICU, which helps cold start. That is a benefit rather than the
reason: the determinism above is what this buys, and a faster start is what it happens to cost
nothing.

Culture-aware behaviour is unavailable repository-wide, not merely discouraged. `RegionInfo` throws,
culture-specific formatting silently falls back to the invariant culture, and a future requirement for
localised output — an operator-facing message in a second language, a currency formatted for a
region — is a change to this record rather than a call-site decision.

Every test suite inherits it, including the ones that talk to containers and to AWS. A library added
later that depends on culture data will fail the same way everywhere rather than only in the account,
which is the direction worth being wrong in.

## Alternatives considered

| Alternative | Why it was rejected |
| --- | --- |
| Leave globalization at the default | Culture data varies with the ICU version present, so the same event can hash differently on a laptop and in the account. The failure arrives as a conflict on a valid republish, which is the one outcome the correctness model exists to prevent. |
| Set it on the function project only | The suites would then certify code running in a different globalization mode from the deployed one. A culture-sensitive comparison would pass every test and fail in the account, which is worse than not setting it at all. |
| Pass `CultureInfo.InvariantCulture` at each call site | Correct where it is remembered. Analyzers cover the formatting calls and not the comparison ones, so the guarantee would be as good as the last review — against a property that cannot be forgotten. |
| Pin an ICU version instead | Makes the data consistent without making it unnecessary, and adds a deployment artefact to keep in step with the runtime. Nothing here needs culture data at all. |
