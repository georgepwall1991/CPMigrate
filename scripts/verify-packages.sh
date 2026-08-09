#!/usr/bin/env bash
set -euo pipefail

package_dir="${1:-artifacts/packages}"
version="$(dotnet msbuild CPMigrate/CPMigrate.csproj -getProperty:Version -nologo | tr -d '[:space:]')"
package="$package_dir/CPMigrate.${version}.nupkg"

if [[ ! -f "$package" ]]; then
  echo "Missing package: $package" >&2
  echo "Pack first: dotnet pack CPMigrate/CPMigrate.csproj -c Release -o $package_dir" >&2
  exit 1
fi

cmp README.md <(unzip -p "$package" README.md)

for asset in \
  assets/flow-analyze-scoreboard.svg \
  assets/flow-cpm-migration.svg \
  assets/flow-update-bisect.svg
do
  cmp "$asset" <(unzip -p "$package" "$asset")
done

nuspec="$(unzip -p "$package" CPMigrate.nuspec)"

for term in \
  "Central Package Management" \
  "Directory.Packages.props" \
  "CPM" \
  "dependency" \
  "rollback" \
  "bisect" \
  "migrator" \
  "vulnerability" \
  "Directory.Build.props"
do
  # Do not pipe a large payload into grep -q under pipefail: grep exits after the match and
  # printf can then receive SIGPIPE on Linux, turning a present term into a false failure.
  grep -Fq "$term" <<<"$nuspec" || {
    echo "Nuspec missing discoverability term: $term" >&2
    exit 1
  }
done

readme_in_pkg="$(unzip -p "$package" README.md)"
for asset in \
  assets/flow-analyze-scoreboard.svg \
  assets/flow-cpm-migration.svg \
  assets/flow-update-bisect.svg
do
  grep -Fq "https://raw.githubusercontent.com/georgepwall1991/CPMigrate/main/${asset}" <<<"$readme_in_pkg" || {
    echo "Packaged README missing absolute HTTPS URL for ${asset}" >&2
    exit 1
  }
done

echo "Verified CPMigrate ${version}: README, assets, and discoverability nuspec terms."
