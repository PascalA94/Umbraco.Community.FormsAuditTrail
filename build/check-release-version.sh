#!/usr/bin/env bash
# Release gate. One tag packs both Umbraco lines: release/<PackageMinor>.<PackagePatch>[-suffix] (suffix optional), for
# example release/1.2 gives 17.1.2 and 18.1.2, and release/1.3-alpha.1 gives 17.1.3-alpha.1 and 18.1.3-alpha.1.
# Fails if Directory.Build.props disagrees with the tag, or if a client version file is not the 0.0.0 placeholder (the
# pack step stamps the real version into the nupkg's manifest, so git never carries one).
# Usage: build/check-release-version.sh release/1.2. Run from the repository root.
set -euo pipefail

tag="${1:?usage: check-release-version.sh <release/minor.patch[-suffix]>}"
pkg="src/Umbraco.Community.FormsAuditTrail"
status=0
sfx='(-[0-9A-Za-z][0-9A-Za-z.-]*)?'

if ! [[ "$tag" =~ ^release/[0-9]+\.[0-9]+${sfx}$ ]]; then
  echo "check-release-version: unexpected tag '$tag'" >&2
  exit 1
fi
want="${tag#release/}"

prop() { sed -n "s:.*<$1>\(.*\)</$1>.*:\1:p" Directory.Build.props | head -1; }
minor="$(prop PackageMinor)"; patch="$(prop PackagePatch)"; suffix="$(prop PackageVersionSuffix)"
have="$minor.$patch${suffix:+-$suffix}"
if [ "$have" != "$want" ]; then
  echo "check-release-version: Directory.Build.props has '$have', tag wants '$want'" >&2
  status=1
fi

for f in "$pkg/Client/package.json" "$pkg/Client/public/umbraco-package.json" "$pkg/wwwroot/umbraco-package.json" "$pkg/Client/package-lock.json"; do
  got="$(node -p "require('./$f').version")"
  if [ "$got" != "0.0.0" ]; then
    echo "check-release-version: $f has '$got', expected the 0.0.0 placeholder" >&2
    status=1
  fi
done
got="$(node -p "require('./$pkg/Client/package-lock.json').packages[''].version")"
if [ "$got" != "0.0.0" ]; then
  echo "check-release-version: $pkg/Client/package-lock.json (root package) has '$got', expected the 0.0.0 placeholder" >&2
  status=1
fi

[ "$status" -ne 0 ] || echo "check-release-version: $want"
exit $status
