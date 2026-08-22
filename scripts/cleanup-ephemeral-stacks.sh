#!/usr/bin/env bash
#
# Remove ephemeral end-to-end stacks that outlived the run that created them.
#
#   Dry run (default):  ./scripts/cleanup-ephemeral-stacks.sh
#   Apply:              ./scripts/cleanup-ephemeral-stacks.sh --execute
#   Keep a longer tail: ./scripts/cleanup-ephemeral-stacks.sh --older-than 12
#
# e2e.yml destroys its stack in a step that runs whether the tests passed or not, so this should
# normally find nothing. It exists for the runs that never reached that step: a cancelled workflow, a
# runner that died, an expired token mid-teardown. Nothing else deletes them, and each one holds two
# queues, two tables and a log group that bill quietly.
#
# Deletion is by age, not by state. A stack whose run is still going is younger than the window; a
# stack older than the window either finished or is never going to. The window defaults to six hours,
# which is well past e2e.yml's own timeout of one.
#
# It only ever considers stacks named ReliableOrders-e2e-*, which is the family
# EnvironmentConfig.Ephemeral produces. The deployed environments are outside that pattern, and this
# script has no way to name them.

set -euo pipefail

EXECUTE=0
OLDER_THAN_HOURS=6
PREFIX="ReliableOrders-e2e-"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --execute) EXECUTE=1; shift ;;
    --older-than)
      [[ $# -ge 2 && -n "$2" ]] || { echo "error: --older-than needs a value in hours" >&2; exit 2; }
      OLDER_THAN_HOURS="$2"; shift 2 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done

command -v aws >/dev/null 2>&1 || { echo "error: aws not found" >&2; exit 1; }

[[ $EXECUTE -eq 0 ]] && echo "=== DRY RUN — re-run with --execute to delete. ===" && echo

cutoff=$(date -u -d "$OLDER_THAN_HOURS hours ago" +%Y-%m-%dT%H:%M:%SZ)

echo "Ephemeral stacks created before $cutoff:"

# DELETE_COMPLETE is excluded rather than filtered afterwards: a deleted stack is still listed for
# ninety days, and a run that reported each of them as a candidate would be a run nobody reads.
stacks=$(aws cloudformation list-stacks \
  --stack-status-filter CREATE_COMPLETE CREATE_FAILED UPDATE_COMPLETE UPDATE_ROLLBACK_COMPLETE ROLLBACK_COMPLETE DELETE_FAILED \
  --query "StackSummaries[?starts_with(StackName, '$PREFIX') && CreationTime < '$cutoff'].StackName" \
  --output text)

if [[ -z "$stacks" ]]; then
  echo "  none"
  exit 0
fi

for stack in $stacks; do
  if [[ $EXECUTE -eq 1 ]]; then
    echo "  deleting $stack"
    aws cloudformation delete-stack --stack-name "$stack"
  else
    echo "  would delete $stack"
  fi
done

echo
echo "Deletion is asynchronous. Watch one with:"
echo "  aws cloudformation describe-stacks --stack-name <name> --query 'Stacks[0].StackStatus'"
