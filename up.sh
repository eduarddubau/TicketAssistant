#!/usr/bin/env bash
# Starts the stack in the foreground, showing each stage: GPU detection, image download,
# build, container start, and the first-run model download. Returns when everything is
# actually ready to chat.
#
#   ./up.sh              # GPU auto-detected; offers one-time GPU setup if needed
#   ./up.sh --build      # extra args are passed through to `podman compose up -d`
#
# GPU: attaching a device can't be "best effort" — podman refuses to create the container
# if the device isn't available — so the try-GPU-else-CPU decision is made here, where
# failure is detectable, instead of in the compose file. An explicit OLLAMA_GPU_DEVICE in
# the environment or .env always wins.
set -euo pipefail
cd "$(dirname "$0")"

step() { printf '\n\033[1m==> %s\033[0m\n' "$*"; }

# Registry pulls occasionally stall indefinitely (observed: a pull inside compose hanging
# with zero bytes moving and no error). Each download step therefore runs under a timeout
# and is retried a few times — worst case the script fails loudly after several attempts
# instead of hanging silently. Completed layers are kept between attempts.
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

has_cdi_spec() { ls /etc/cdi/nvidia*.{yaml,json} >/dev/null 2>&1; }

# One-time host setup for GPU containers (Fedora/RHEL). Called only after the user says
# yes — it adds NVIDIA's repo and installs packages via sudo, which should be an explicit
# choice. Idempotent: each part checks before changing anything. Returns 0 when the CDI
# spec exists at the end.
setup_gpu() {
  if ! command -v dnf >/dev/null 2>&1; then
    echo "Automated setup covers Fedora/RHEL (dnf) — for other distros, see README \"GPU acceleration\"."
    return 1
  fi

  if ! lsmod | grep -q '^nvidia'; then
    echo "Installing the NVIDIA driver (akmod-nvidia, from RPM Fusion) + CUDA tools..."
    sudo dnf install -y akmod-nvidia xorg-x11-drv-nvidia-cuda
    echo "Driver installed. Reboot, then run ./up.sh again to finish GPU setup."
    return 1
  fi

  if ! command -v nvidia-smi >/dev/null 2>&1; then
    echo "Installing the CUDA userspace tools (xorg-x11-drv-nvidia-cuda)..."
    sudo dnf install -y xorg-x11-drv-nvidia-cuda
  fi

  if ! command -v nvidia-ctk >/dev/null 2>&1; then
    echo "Installing the NVIDIA container toolkit (from NVIDIA's repo)..."
    curl -s -L https://nvidia.github.io/libnvidia-container/stable/rpm/nvidia-container-toolkit.repo \
      | sudo tee /etc/yum.repos.d/nvidia-container-toolkit.repo >/dev/null
    sudo dnf install -y nvidia-container-toolkit
  fi

  if ! has_cdi_spec; then
    echo "Generating the CDI spec (/etc/cdi/nvidia.yaml)..."
    sudo nvidia-ctk cdi generate --output=/etc/cdi/nvidia.yaml
  fi

  has_cdi_spec
}

step "1/5 Checking for an NVIDIA GPU"
if [ -n "${OLLAMA_GPU_DEVICE:-}" ] || grep -qs '^OLLAMA_GPU_DEVICE=.\+' .env 2>/dev/null; then
  echo "OLLAMA_GPU_DEVICE is set explicitly — using it as-is."
elif has_cdi_spec; then
  export OLLAMA_GPU_DEVICE=nvidia.com/gpu=all
  echo "NVIDIA CDI spec found — the GPU will be attached to Ollama."
elif lspci 2>/dev/null | grep -qi nvidia; then
  echo "This machine has an NVIDIA GPU, but the container GPU support isn't set up yet."
  if [ -t 0 ]; then
    read -r -p "Set it up now? (installs NVIDIA's container toolkit, asks for your sudo password) [y/N] " answer || answer=""
    if [[ ${answer} =~ ^[Yy] ]] && setup_gpu; then
      export OLLAMA_GPU_DEVICE=nvidia.com/gpu=all
      echo "GPU setup complete — the GPU will be attached to Ollama."
    else
      echo "Continuing on the CPU. Run ./up.sh again anytime to be re-offered GPU setup."
    fi
  else
    echo "  (Non-interactive run — continuing on the CPU. Run ./up.sh from a terminal to be offered setup.)"
  fi
else
  echo "No NVIDIA GPU found — Ollama will run on the CPU."
fi

step "2/5 Downloading the Ollama runtime image (cached after the first run)"
retry 3 1200 podman pull docker.io/ollama/ollama:latest

step "3/5 Building the assistant and the mock ticketing system"
retry 3 1200 podman compose build

step "4/5 Starting the containers"
podman compose up -d "$@"

step "5/5 Downloading the chat model (a couple of GB on first run, instant after)"
puller=$(podman ps -a --format '{{.Names}}' | grep ollama-pull | head -1)
podman logs -f "$puller" 2>&1 || true
rc=$(podman inspect --format '{{.State.ExitCode}}' "$puller")
if [ "$rc" != "0" ]; then
  echo "Model download failed (exit $rc) — see above. Re-run ./up.sh to retry." >&2
  exit 1
fi

printf '\n\033[1m✔ Ready.\033[0m\n'
echo "  Chat console  → http://localhost:5080/"
echo "  Ticket board  → http://localhost:5090/"
