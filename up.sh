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

# Every ending — success or any failure — lands here via the EXIT trap: print a clear
# banner (the error text itself is in the output just above) so the outcome is obvious,
# and tell the user the window is safe to close. Non-interactive runs skip that note.
finish() {
  status=$?
  echo
  if [ "$status" -eq 0 ]; then
    printf '\033[1;32m✔ Success — everything is up and ready.\033[0m\n'
    echo "  Console       → http://localhost:4200/   (the app — start here)"
    echo "  Ticket board  → http://localhost:5090/"
  else
    printf '\033[1;31m✘ Startup failed (exit %s) — the error is in the output above.\033[0m\n' "$status"
  fi
  if [ -t 0 ]; then
    # The terminal window closes the moment this script exits, taking the outcome with
    # it — so don't exit: idle until the user closes the window themselves. Ctrl+C (for
    # shell runs) is caught and exits with the run's real status — otherwise bash would
    # exit 130 and GUI terminals flag that as an abnormal end.
    echo "You may close this window (Ctrl+C returns to the shell)."
    trap 'exit '"$status" INT
    sleep infinity || true
  fi
  exit "$status"
}
trap finish EXIT

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

# Note: no brace expansion here — `ls a b` fails if EITHER pattern matches nothing, so
# checking nvidia*.{yaml,json} would report "no spec" when only nvidia.yaml exists.
has_cdi_spec() { ls /etc/cdi/nvidia* >/dev/null 2>&1; }

# Reads a variable out of .env. Compose reads that file itself, but this script has to know
# what was asked for to check the running container against it.
dotenv() { sed -n "s/^$1=//p" .env 2>/dev/null | head -1 | tr -d "\"'\r" | sed 's/^ *//; s/ *$//'; }

ollama_container() { podman ps -a --format '{{.Names}}' | grep -m1 -E '[-_]ollama[-_]1$' || true; }

# podman-compose never re-creates an existing container: `up -d` just starts what's already
# there, devices and environment included. So an ollama container built before the GPU was
# set up would quietly stay on the CPU — and keep whatever idle-unload timer it was created
# with — however carefully this script detects a GPU. Compare what the existing container
# has against what's being asked for, so step 4 can re-create it when they differ.
ollama_stale() {
  local name devices keepalive want_keepalive has_gpu=no want_gpu=no
  name=$(ollama_container)
  [ -n "$name" ] || return 1   # nothing there yet — compose will create it with today's settings
  devices=$(podman inspect "$name" --format '{{range .HostConfig.Devices}}{{.PathOnHost}} {{end}}' 2>/dev/null || true)
  keepalive=$(podman inspect "$name" --format '{{range .Config.Env}}{{println .}}{{end}}' 2>/dev/null \
    | sed -n 's/^OLLAMA_KEEP_ALIVE=//p' | head -1)

  # CDI expands nvidia.com/gpu=all into the individual /dev/nvidia* nodes, so the device list
  # is compared as "has NVIDIA nodes or not" rather than against the request verbatim.
  case "$devices" in *nvidia*) has_gpu=yes ;; esac
  case "${OLLAMA_GPU_DEVICE:-}" in ""|/dev/null) ;; *) want_gpu=yes ;; esac

  want_keepalive=${OLLAMA_KEEP_ALIVE:-$(dotenv OLLAMA_KEEP_ALIVE)}
  [ "$has_gpu" != "$want_gpu" ] || [ "$keepalive" != "${want_keepalive:--1}" ]
}

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

step "1/6 Checking for an NVIDIA GPU"
# An explicit setting always wins; the .env one is pulled into the environment so the rest
# of the script knows which device the containers are getting.
if [ -z "${OLLAMA_GPU_DEVICE:-}" ]; then
  export OLLAMA_GPU_DEVICE=$(dotenv OLLAMA_GPU_DEVICE)
fi
if [ -n "${OLLAMA_GPU_DEVICE:-}" ]; then
  echo "OLLAMA_GPU_DEVICE is set explicitly ($OLLAMA_GPU_DEVICE) — using it as-is."
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

step "2/6 Downloading the Ollama runtime image (cached after the first run)"
retry 3 1200 podman pull docker.io/ollama/ollama:latest

step "3/6 Building the assistant and the mock ticketing system"
retry 3 1200 podman compose build

step "4/6 Starting the containers"
# podman-compose occasionally trips over recreating an exited one-shot container
# ("no container ... found", exit 125); a single retry settles it.
compose_up() {
  if ! podman compose up -d "$@"; then
    echo "compose up hit a transient error — retrying once..."
    sleep 2
    podman compose up -d "$@"
  fi
}
# --force-recreate covers the whole stack (podman-compose doesn't honour a per-service
# filter here), which is fine: everything is stateless — Ollama's models live in a volume,
# and the model itself is re-loaded by the warm-up in step 5.
if ollama_stale; then
  echo "The existing Ollama container has different GPU/keep-alive settings — re-creating it."
  compose_up --force-recreate "$@"
else
  compose_up "$@"
fi

step "5/6 Downloading the chat model (a couple of GB on first run, instant after)"
puller=$(podman ps -a --format '{{.Names}}' | grep ollama-pull | head -1)
podman logs -f "$puller" 2>&1 || true
rc=$(podman inspect --format '{{.State.ExitCode}}' "$puller")
if [ "$rc" != "0" ]; then
  echo "Model download failed (exit $rc) — see above. Re-run ./up.sh to retry." >&2
  exit 1
fi

step "6/6 Checking what Ollama ended up running"
# What actually happened, rather than what was asked for: PROCESSOR says GPU or CPU, and
# UNTIL "Forever" confirms the model stays loaded instead of being unloaded when idle.
ollama=$(ollama_container)
if [ -n "$ollama" ]; then
  loaded=$(podman exec "$ollama" ollama ps 2>&1 || true)
  printf '%s\n' "$loaded"
  case "$loaded" in
    *GPU*) echo "The model is loaded on the GPU and stays loaded — the first message is instant." ;;
    *)     echo "The model is loaded on the CPU (replies will be slower). See README \"GPU acceleration\"." ;;
  esac
fi
# Success/fail banner + keep-window-open prompt are printed by finish() (EXIT trap).
