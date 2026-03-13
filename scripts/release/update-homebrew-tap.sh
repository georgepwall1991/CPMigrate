#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 2 ]]; then
  echo "usage: $0 <formula-path> <token>" >&2
  exit 1
fi

FORMULA_PATH="$1"
TOKEN="$2"
TAP_REPO="https://x-access-token:${TOKEN}@github.com/georgepwall1991/homebrew-cpmigrate.git"
WORKDIR="$(mktemp -d)"
trap 'rm -rf "${WORKDIR}"' EXIT

git clone "${TAP_REPO}" "${WORKDIR}/tap"
mkdir -p "${WORKDIR}/tap/Formula"
cp "${FORMULA_PATH}" "${WORKDIR}/tap/Formula/cpmigrate.rb"

pushd "${WORKDIR}/tap" >/dev/null
if git diff --quiet -- Formula/cpmigrate.rb; then
  echo "Homebrew tap already up to date."
  exit 0
fi

git config user.name "github-actions[bot]"
git config user.email "41898282+github-actions[bot]@users.noreply.github.com"
git add Formula/cpmigrate.rb
git commit -m "Update CPMigrate formula"
git push origin main
popd >/dev/null
