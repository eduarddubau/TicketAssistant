#!/usr/bin/env bash
# Runs the up.ps1 checks (tools/test-up.ps1) inside the official PowerShell image, so up.ps1
# can be verified from Linux or macOS without installing PowerShell and without starting any
# of the app's containers — the checks talk to a fake podman.
#
#   ./tools/test-up-ps1.sh              # parse + unit + dry-run checks
#   ./tools/test-up-ps1.sh -Analyze     # also PSScriptAnalyzer (downloads the module)
#
# Set CONTAINER_ENGINE=docker to use docker instead of podman.
set -euo pipefail
cd "$(dirname "$0")/.."

engine=${CONTAINER_ENGINE:-podman}
image=mcr.microsoft.com/powershell:latest

if ! "$engine" image exists "$image" 2>/dev/null && ! "$engine" image inspect "$image" >/dev/null 2>&1; then
  echo "Pulling $image (once)..."
  "$engine" pull "$image"
fi

# The repo goes in read-only — nothing here should be able to modify the working tree. Label
# relabelling (:z) is deliberately not used: it would rewrite the SELinux labels of the whole
# repo on Fedora/RHEL, so the container's labelling is switched off instead, exactly as the
# compose file does for its own bind mounts.
exec "$engine" run --rm \
  -v "$PWD":/work:ro \
  --security-opt label=disable \
  -w /work \
  "$image" pwsh -NoProfile -File /work/tools/test-up.ps1 "$@"
