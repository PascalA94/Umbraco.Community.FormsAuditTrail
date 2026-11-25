#!/usr/bin/env bash
# Release and PR gate: fail if any project resolves a package with a known advisory,
# direct or transitive. Parses the JSON output; never trusts the command's exit code.
# Usage: build/check-vulnerable.sh [solution-or-project]   (default: the package solution)
set -euo pipefail

TARGET="${1:-src/Umbraco.Community.FormsAuditTrail.slnx}"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
OUT="$(mktemp)"
trap 'rm -f "$OUT"' EXIT

dotnet list "$TARGET" package --vulnerable --include-transitive --format json > "$OUT" || true

python3 -I "$HERE/check-vulnerable.py" "$OUT"
