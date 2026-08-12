#!/usr/bin/env bash
# Fetches the canonical CodeRouterBench tables (PLAN.md Phase K) from the published Hugging Face
# dataset into data/coderouterbench/, then verifies each file's row count against the counts
# docs/HANDBOOK.md and PLAN.md assert (9,999 x 8 = 79,992; 7,080 x 8 = 56,640; 2,919 x 8 = 23,352;
# 176 x 8 = 1,408), failing loudly on any mismatch rather than leaving a silently-truncated or
# silently-stale file in place.
#
# The data is fetched on demand rather than checked in: at ~137 MB it is large for a git checkout,
# and the dataset already has a stable, versioned home on Hugging Face. Re-run this script any time
# data/coderouterbench/ is missing or you want to refresh it from the dataset's current `main`.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DEST_DIR="$REPO_ROOT/data/coderouterbench"
BASE_URL="https://huggingface.co/datasets/Lance1573/CodeRouterBench/resolve/main"

mkdir -p "$DEST_DIR"

# file:expected_data_row_count (0 means "no row-count check", e.g. it isn't a header+rows table).
FILES=(
  "id_results_long.csv:79992"
  "id_probing_results_long.csv:56640"
  "id_test_results_long.csv:23352"
  "ood176_results_long.csv:1408"
  "id_tasks.jsonl:9999"
  "ood176_tasks.jsonl:176"
  "models.json:0"
  "summary.json:0"
)

fail() {
  echo "fetch-coderouterbench: $1" >&2
  exit 1
}

for entry in "${FILES[@]}"; do
  name="${entry%%:*}"
  expected_rows="${entry##*:}"
  dest="$DEST_DIR/$name"
  tmp="$dest.tmp"

  echo "Fetching $name..."
  if ! curl -fsSL "$BASE_URL/$name" -o "$tmp"; then
    rm -f "$tmp"
    fail "download of $name failed"
  fi

  case "$name" in
    *.csv)
      # Data rows = total lines minus the header line.
      actual_rows=$(($(wc -l < "$tmp") - 1))
      ;;
    *.jsonl)
      actual_rows=$(wc -l < "$tmp")
      ;;
    *)
      actual_rows=0
      ;;
  esac

  if [ "$expected_rows" -ne 0 ] && [ "$actual_rows" -ne "$expected_rows" ]; then
    rm -f "$tmp"
    fail "$name has $actual_rows data row(s), expected $expected_rows - refusing to leave a corrupt/stale file in place"
  fi

  mv "$tmp" "$dest"
  echo "  OK: $name ($actual_rows data row(s))"
done

echo "CodeRouterBench restored to $DEST_DIR"
