#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 3 ]]; then
  echo "usage: $0 <version> <package-url> <sha256>" >&2
  exit 1
fi

VERSION="$1"
VERSION_REGEX="${VERSION//./\\.}"
PACKAGE_URL="$2"
PACKAGE_SHA="$3"

cat <<EOF
class Cpmigrate < Formula
  desc "NuGet Central Package Management migration and dependency analysis tool for .NET teams"
  homepage "https://georgepwall1991.github.io/CPMigrate/"
  url "${PACKAGE_URL}"
  sha256 "${PACKAGE_SHA}"
  license "MIT"

  def install
    nupkg = Dir["*.nupkg"].fetch(0)
    mkdir "pkg"
    system "unzip", "-q", nupkg, "-d", "pkg"
    libexec.install Dir["pkg/tools/net10.0/any/*"]
    (bin/"cpmigrate").write <<~EOS
      #!/bin/bash
      exec "dotnet" "#{libexec}/CPMigrate.dll" "\$@"
    EOS
    chmod 0755, bin/"cpmigrate"
  end

  test do
    # CommandLineParser writes version/help text to stderr. Capture both streams so the formula's
    # own test checks the installed tool instead of comparing an empty stdout string.
    output = shell_output("#{bin}/cpmigrate --version 2>&1")
    assert_match(/\ACPMigrate ${VERSION_REGEX}(?:\+\S+)?\s*\z/, output)
  end
end
EOF
