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

if [ -z "${OLLAMA_GPU_DEVICE:-}" ] && ! grep -qs '^OLLAMA_GPU_DEVICE=.\+' .env 2>/dev/null; then
  if ls /etc/cdi/nvidia*.{yaml,json} >/dev/null 2>&1; then
    export OLLAMA_GPU_DEVICE=nvidia.com/gpu=all
    echo "NVIDIA CDI spec found — attaching GPU to Ollama (set OLLAMA_GPU_DEVICE in .env to override)."
  else
    echo "No NVIDIA CDI spec found — Ollama will run on the CPU."
  fi
fi

exec podman compose up -d "$@"
