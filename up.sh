#!/usr/bin/env bash
# Starts the stack, attaching the host's NVIDIA GPU to Ollama automatically when the host
# is set up for it. A GPU device can't be attached "best effort" — podman refuses to create
# the container if the device isn't available — so the try-GPU-else-CPU decision is made
# here, where failure is detectable, instead of in the compose file.
#
#   ./up.sh              # up -d, GPU auto-detected
#   ./up.sh --build      # extra args are passed through to `podman compose up -d`
#
# Detection: a generated NVIDIA CDI spec (see README "GPU acceleration" for the one-time
# host setup). An explicit OLLAMA_GPU_DEVICE in the environment or .env always wins.
set -euo pipefail
cd "$(dirname "$0")"

# --- GPU auto-detect ---------------------------------------------------------------------

if [ -z "${OLLAMA_GPU_DEVICE:-}" ] && ! grep -qs '^OLLAMA_GPU_DEVICE=.\+' .env 2>/dev/null; then
  if ls /etc/cdi/nvidia*.{yaml,json} >/dev/null 2>&1; then
    export OLLAMA_GPU_DEVICE=nvidia.com/gpu=all
    echo "NVIDIA CDI spec found — attaching GPU to Ollama (set OLLAMA_GPU_DEVICE in .env to override)."
  else
    echo "No NVIDIA CDI spec found — Ollama will run on the CPU."
  fi
fi

# --- Downloads with retry ----------------------------------------------------------------
# Registry pulls occasionally stall indefinitely (observed: a pull inside compose hanging
# with zero bytes moving and no error). Each download step therefore runs under a timeout
# and is retried a few times, so a transient blip doesn't require any debugging — worst
# case the script fails loudly after several attempts instead of hanging silently.

retry() { # retry <attempts> <timeout-seconds> <cmd...>
  local attempts=$1 timeout_s=$2 i
  shift 2
  for i in $(seq 1 "$attempts"); do
    if timeout "$timeout_s" "$@"; then
      return 0
    fi
    echo "'$*' failed or stalled (attempt $i/$attempts) — retrying in 5s..." >&2
    sleep 5
  done
  echo "Giving up on '$*' after $attempts attempts." >&2
  return 1
}

# Pull the one big external image up front — this is the pull most likely to stall, and
# inside compose a stall gives no feedback. Completed layers are kept between attempts.
retry 3 1200 podman pull docker.io/ollama/ollama:latest

# Build the two services (downloads the dotnet base images on a fresh host).
retry 3 1200 podman compose build

exec podman compose up -d "$@"
