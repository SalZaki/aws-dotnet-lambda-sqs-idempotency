#!/usr/bin/env bash
#
# Render every docs/diagrams/*.mmd to a committed SVG beside it.
#
#   ./scripts/render-diagrams.sh           render
#   ./scripts/render-diagrams.sh --check   fail if any SVG is stale or missing
#
# The .mmd files are the source of truth. The .svg files are generated and
# committed so the diagrams render in pull request diffs, IDEs, and anything
# else that consumes raw markdown, none of which run GitHub's mermaid renderer.
#
# Requires Node. mermaid-cli downloads a headless Chromium on first run.

set -euo pipefail

cd "$(dirname "$0")/.."
DIR=docs/diagrams
CHECK=0
[[ "${1:-}" == "--check" ]] && CHECK=1

command -v npx >/dev/null 2>&1 || { echo "error: node/npx not found" >&2; exit 1; }

# Chromium refuses to run as root without these, and CI images often are root.
PUPPETEER_CFG="$(mktemp)"
trap 'rm -f "$PUPPETEER_CFG"' EXIT
cat > "$PUPPETEER_CFG" <<'JSON'
{ "args": ["--no-sandbox", "--disable-setuid-sandbox", "--disable-dev-shm-usage"] }
JSON

status=0
for src in "$DIR"/*.mmd; do
  out="${src%.mmd}.svg"

  if [[ $CHECK -eq 1 ]]; then
    tmp="$(mktemp --suffix=.svg)"
    npx --yes -p @mermaid-js/mermaid-cli mmdc -p "$PUPPETEER_CFG" \
        -i "$src" -o "$tmp" -b transparent >/dev/null 2>&1
    if [[ ! -f "$out" ]]; then
      echo "  MISSING  $out" >&2; status=1
    elif ! diff -q "$tmp" "$out" >/dev/null 2>&1; then
      echo "  STALE    $out does not match $src" >&2; status=1
    else
      echo "  ok       $out"
    fi
    rm -f "$tmp"
  else
    npx --yes -p @mermaid-js/mermaid-cli mmdc -p "$PUPPETEER_CFG" \
        -i "$src" -o "$out" -b transparent >/dev/null
    echo "  rendered $out"
  fi
done

if [[ $CHECK -eq 1 && $status -ne 0 ]]; then
  echo >&2
  echo "Diagrams are out of date. Run ./scripts/render-diagrams.sh and commit the result." >&2
fi
exit $status
