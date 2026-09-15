#!/usr/bin/env bash
# Idempotent Cloud Agent bootstrap for CPMigrate.
# Installs the .NET 10 SDK (matching CI's 10.0.x) and restores NuGet dependencies.
set -euo pipefail

DOTNET_DIR="${HOME}/.dotnet"
DOTNET_CHANNEL="10.0"

need_install=true
if [ -x "${DOTNET_DIR}/dotnet" ] && "${DOTNET_DIR}/dotnet" --list-sdks 2>/dev/null | grep -q '^10\.'; then
  need_install=false
fi

if [ "${need_install}" = true ]; then
  echo "Installing .NET SDK ${DOTNET_CHANNEL} into ${DOTNET_DIR}..."
  tmp_script="$(mktemp)"
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o "${tmp_script}"
  chmod +x "${tmp_script}"
  "${tmp_script}" --channel "${DOTNET_CHANNEL}" --install-dir "${DOTNET_DIR}"
  rm -f "${tmp_script}"
else
  echo ".NET 10 SDK already present in ${DOTNET_DIR}; skipping install."
fi

# Expose dotnet on PATH for every future shell. The muxer resolves its root via
# the real path of the binary, so the symlink keeps DOTNET_ROOT correct.
sudo ln -sf "${DOTNET_DIR}/dotnet" /usr/local/bin/dotnet

export DOTNET_ROOT="${DOTNET_DIR}"
export PATH="${DOTNET_DIR}:${DOTNET_DIR}/tools:${PATH}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

dotnet --info

cd "$(dirname "$0")/.."
dotnet restore CPMigrate.sln
