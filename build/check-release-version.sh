#!/usr/bin/env bash
# Release gate: every version file must equal the tag's version, so NuGet never ships a backoffice
# manifest with a stale version. Usage: build/check-release-version.sh v18.0.4. Run from the repository root.
set -euo pipefail

tag="${1:?usage: check-release-version.sh <tag>}"
want="${tag#v}"
pkg="src/Umbraco.Community.FormsAuditTrail"
status=0

check() {
  local file="$1" got="$2"
  if [ "$got" != "$want" ]; then
    echo "check-release-version: $file has '$got', tag wants '$want'" >&2
    status=1
  fi
}

check "$pkg/Umbraco.Community.FormsAuditTrail.csproj" "$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$pkg/Umbraco.Community.FormsAuditTrail.csproj" | head -1)"
for f in "$pkg/Client/package.json" "$pkg/Client/public/umbraco-package.json" "$pkg/wwwroot/umbraco-package.json"; do
  check "$f" "$(node -p "require('./$f').version")"
done
check "$pkg/Client/package-lock.json" "$(node -p "require('./$pkg/Client/package-lock.json').version")"
check "$pkg/Client/package-lock.json (root package)" "$(node -p "require('./$pkg/Client/package-lock.json').packages[''].version")"

exit $status
