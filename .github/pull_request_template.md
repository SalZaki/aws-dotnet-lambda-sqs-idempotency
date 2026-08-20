<!--
Delete what does not apply. A one-line fix does not need every heading; a change to a documented
decision needs all of them.
-->

Closes #

## What changed

## Why

<!--
The half that is not in the diff. What failed before, what the alternative was and why it lost, and
what this costs. Where a claim can be measured, measure it and say what came back.
-->

## Checks

- [ ] Tests cover the behaviour, and a test that would have caught the defect exists.
- [ ] Documentation under `docs/` matches, with a [revision log](../docs/revision-log.md) entry where
      a documented decision changed.
- [ ] Lock files regenerated with `--force-evaluate` if a package version moved.
- [ ] cdk-nag findings fixed, or accepted on the resource with a reason and pinned in the test.
