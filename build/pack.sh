#!/usr/bin/env bash
# Packs one nupkg per Umbraco line in eng/lines.txt into <outdir> (default ./artifacts), each with the client bundle.
# The client version files stay 0.0.0 in git; this stamps the line's package version into wwwroot/umbraco-package.json
# for the build and pack, then restores it, so the committed tree never changes (the backoffice shows that version).
# Usage: bash build/pack.sh [outdir]. Run from the repository root, after the client build and the tests.
set -euo pipefail
cd "$(dirname "$0")/.."
out="${1:-./artifacts}"
proj=src/Umbraco.Community.FormsAuditTrail/Umbraco.Community.FormsAuditTrail.csproj
manifest=src/Umbraco.Community.FormsAuditTrail/wwwroot/umbraco-package.json
backup="$(mktemp)"
cp "$manifest" "$backup"
trap 'cp "$backup" "$manifest"; rm -f "$backup"' EXIT
mkdir -p "$out"

while read -r line; do
  [ -n "$line" ] || continue
  version="$(dotnet msbuild "$proj" "-p:UmbracoMajor=$line" -getProperty:Version | tr -d '[:space:]')"
  case "$version" in ''|*-) echo "invalid package version '$version' for line $line" >&2; exit 1;; esac
  node -e '
    const fs = require("fs"); const [f, v] = process.argv.slice(1);
    const j = JSON.parse(fs.readFileSync(f, "utf8")); j.version = v;
    fs.writeFileSync(f, JSON.stringify(j, null, 2) + "\n");' "$manifest" "$version"
  dotnet restore "$proj" "-p:UmbracoMajor=$line" --locked-mode -warnaserror:NU1903,NU1904 -v q
  dotnet build "$proj" -c Release --no-restore "-p:UmbracoMajor=$line" -v q
  dotnet pack "$proj" -c Release --no-build "-p:UmbracoMajor=$line" -o "$out" -v q
  cp "$backup" "$manifest"

  # The bundle must ship as a static web asset and no lock file may leak into the package.
  nupkg="$out/Umbraco.Community.FormsAuditTrail.$version.nupkg"
  [ -f "$nupkg" ] || { echo "expected $nupkg was not produced" >&2; exit 1; }
  listing="$(unzip -Z1 "$nupkg")"
  grep -qx 'staticwebassets/index.js' <<<"$listing" || { echo "$nupkg lacks staticwebassets/index.js" >&2; exit 1; }
  if grep -q 'lock\.json' <<<"$listing"; then echo "$nupkg contains a lock file" >&2; exit 1; fi
  echo "packed $line: $version"
done < eng/lines.txt
