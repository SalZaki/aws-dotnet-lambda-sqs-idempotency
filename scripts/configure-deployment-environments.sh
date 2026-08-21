#!/usr/bin/env bash
#
# Create the two deployment environments the deploy workflows run in, with the policies that decide
# which refs may reach them, and store the role ARNs the jobs assume.
#
#   Dry run (default):  ./scripts/configure-deployment-environments.sh
#   Apply:              ./scripts/configure-deployment-environments.sh --execute \
#                         --dev-role-arn arn:aws:iam::<account>:role/<dev role> \
#                         --release-role-arn arn:aws:iam::<account>:role/<release role> \
#                         --region eu-west-2
#
# The ARNs come from the DeploymentIdentityStack outputs, which is the stack an administrator
# deploys once by hand:
#
#   cd infra/ReliableOrders.Cdk
#   npx cdk deploy ReliableOrders-DeploymentIdentity
#
# Half the trust lives here rather than in that stack, and deliberately. IAM decides which
# environment may assume which role; only GitHub can decide which ref may reach an environment, and
# a trust policy naming an environment nobody restricted is a trust policy naming every branch.
#
# The role ARN is a secret rather than a variable because it carries the account ID and this
# repository is public. It is not a credential — assuming the role still needs a token GitHub only
# mints for a job running in the named environment.
#
# Safe to re-run: environments and policies are updated in place rather than duplicated.

set -euo pipefail

REPO="SalZaki/aws-dotnet-lambda-sqs-idempotency"
EXECUTE=0
DEV_ROLE_ARN=""
RELEASE_ROLE_ARN=""
REGION=""

# The branch deploy-dev.yml deploys from, and the tag pattern release.yml cuts a release from. Both
# are also asserted in the workflows' own conditions; a policy here is what stops a run reaching the
# environment at all, and the condition is what stops a run that reached it doing anything.
DEV_BRANCH="main"
RELEASE_TAG="v*"

# A flag written last with its value forgotten would otherwise reach `shift 2` with one argument
# left, and `set -e` turns that into an exit status and no message at all.
value() {
  [[ $# -ge 2 && -n "$2" ]] || { echo "error: $1 needs a value" >&2; exit 2; }
  echo "$2"
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --execute) EXECUTE=1; shift ;;
    --dev-role-arn) DEV_ROLE_ARN=$(value "$@"); shift 2 ;;
    --release-role-arn) RELEASE_ROLE_ARN=$(value "$@"); shift 2 ;;
    --region) REGION=$(value "$@"); shift 2 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done

command -v gh >/dev/null 2>&1 || { echo "error: gh not found" >&2; exit 1; }
gh auth status >/dev/null 2>&1 || { echo "error: gh not authenticated" >&2; exit 1; }

[[ $EXECUTE -eq 0 ]] && echo "=== DRY RUN — re-run with --execute to apply. ===" && echo

OWNER="${REPO%%/*}"

# The owner reviews their own releases, because on a repository with one maintainer the alternative
# is an approval nobody can give. prevent_self_review is left off for the same reason; turn it on the
# day a second maintainer exists, which is the day it starts protecting anything.
REVIEWER_ID=$(gh api "users/$OWNER" --jq '.id')

run() {
  if [[ $EXECUTE -eq 1 ]]; then
    "$@"
  else
    printf '  would run:'
    printf ' %q' "$@"
    printf '\n'
  fi
}

environment() {
  local name="$1" reviewers="$2" ref_type="$3" ref="$4"

  echo "$name"

  # custom_branch_policies, not protected_branches. Protected branches would admit every branch the
  # ruleset protects, and a tag policy cannot be expressed that way at all.
  run gh api --method PUT "repos/$REPO/environments/$name" \
    --input - <<JSON
{
  "wait_timer": 0,
  "prevent_self_review": false,
  "reviewers": $reviewers,
  "deployment_branch_policy": { "protected_branches": false, "custom_branch_policies": true }
}
JSON

  # Re-running would otherwise add a second copy of the same rule, and a policy list nobody can read
  # is one nobody audits.
  if [[ $EXECUTE -eq 1 ]]; then
    gh api "repos/$REPO/environments/$name/deployment-branch-policies" --jq '.branch_policies[].id' \
      | while read -r id; do
          gh api --method DELETE "repos/$REPO/environments/$name/deployment-branch-policies/$id"
        done
  fi

  run gh api --method POST "repos/$REPO/environments/$name/deployment-branch-policies" \
    -f "name=$ref" -f "type=$ref_type"
}

# --repo on every write, as every read above carries it. gh resolves a repository from the current
# directory's remote when it is not told one, so a run from a fork's clone would create the
# environments on the repository named at the top of this file and store the role ARN in another —
# and report both as done.
secret() {
  local name="$1" environment="$2" value="$3"

  if [[ -z "$value" ]]; then
    echo "  $name not given. Until the $environment environment holds one, a deployment fails inside"
    echo "  configure-aws-credentials with an empty role rather than here."
    return
  fi

  run gh secret set "$name" --repo "$REPO" --env "$environment" --body "$value"
}

environment "dev" "[]" "branch" "$DEV_BRANCH"
secret "AWS_DEPLOY_ROLE_ARN" "dev" "$DEV_ROLE_ARN"

environment "release" "[{\"type\":\"User\",\"id\":$REVIEWER_ID}]" "tag" "$RELEASE_TAG"
secret "AWS_DEPLOY_ROLE_ARN" "release" "$RELEASE_ROLE_ARN"

# A repository variable rather than a per-environment one: both deployments go to the same Region,
# and it is neither a secret nor something an environment should be able to disagree about.
if [[ -n "$REGION" ]]; then
  echo "AWS_REGION"
  run gh variable set AWS_REGION --repo "$REPO" --body "$REGION"
fi

echo
echo "Done. Read it back with: gh api repos/$REPO/environments --jq '.environments[].name'"
