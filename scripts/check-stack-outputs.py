#!/usr/bin/env python3
"""Check a deployment produced every output the stack publishes, and summarise what it made.

    python3 scripts/check-stack-outputs.py <outputs-file> <stack-name> >> "$GITHUB_STEP_SUMMARY"

The outputs are the deployment's own account of what it made. A missing one means the stack deployed
without something the runbooks and the end-to-end tests reach for by name, which CloudFormation
reports as success.

Names are printed, never values. A queue URL carries the account ID, a step summary on a public
repository is public, and the account-ID masking that covers the log does not cover that file.

A script rather than a step in each workflow: two deployments check the same six names, and the copy
that fell behind would be the one nobody read. ReliableOrders.CdkTests reads the list below and
compares it against the stack's own outputs, so a renamed output fails rather than leaving a check
that passes over a name nothing produces.
"""

import json
import sys

PUBLISHED = [
    "SourceQueueUrl",
    "DeadLetterQueueUrl",
    "OrdersTableName",
    "IdempotencyRecordsTableName",
    "OrderProcessorFunctionName",
    "DashboardName",
]


def main(path: str, stack: str) -> int:
    with open(path, encoding="utf-8") as file:
        outputs = json.load(file).get(stack, {})

    missing = [name for name in PUBLISHED if not str(outputs.get(name, "")).strip()]

    print(f"## Deployment\n\n`{stack}`\n")
    print("| Output | Present |")
    print("| --- | --- |")

    for name in PUBLISHED:
        print(f"| {name} | {'no' if name in missing else 'yes'} |")

    if missing:
        print(
            f"\n{', '.join(missing)} produced no value, so the stack deployed without something "
            "the runbooks name."
        )

    return 1 if missing else 0


if __name__ == "__main__":
    if len(sys.argv) != 3:
        print(__doc__, file=sys.stderr)
        sys.exit(2)

    sys.exit(main(sys.argv[1], sys.argv[2]))
