# Windows counterpart of up.sh: starts the stack in the foreground, showing each stage —
# GPU detection, image download, build, container start, and the first-run model download.
# Returns when everything is actually ready to chat.
#
#   .\up.ps1             # GPU auto-detected; offers one-time GPU setup if needed
#   .\up.ps1 --build     # extra args are passed through to `podman compose up -d`
#
# On Windows, podman runs containers inside a Linux VM (`podman machine`), so the CDI spec
# that exposes the GPU lives *inside that VM* — detection asks the machine, not Windows.
# The Windows NVIDIA driver itself is enough host-side: WSL2 projects it into the VM
# automatically. An explicit OLLAMA_GPU_DEVICE in the environment or .env always wins.
param([Parameter(ValueFromRemainingArguments = $true)][string[]]$ComposeArgs)

Set-Location $PSScriptRoot

function Step([string]$Message) { Write-Host ""; Write-Host "==> $Message" -ForegroundColor White }

# Registry pulls occasionally stall indefinitely. Each download step runs under a timeout
# and is retried a few times — worst case the script fails loudly after several attempts
# instead of hanging silently. Completed layers are kept between attempts.
function Invoke-Retry {
    param([int]$Attempts, [int]$TimeoutSec, [string[]]$Command)
    for ($i = 1; $i -le $Attempts; $i++) {
        $p = Start-Process -FilePath $Command[0] -ArgumentList $Command[1..($Command.Length - 1)] `
            -NoNewWindow -PassThru
        if ($p.WaitForExit($TimeoutSec * 1000) -and $p.ExitCode -eq 0) {
            return
        }
        if (-not $p.HasExited) { $p.Kill() }
        Write-Host "'$($Command -join ' ')' failed or stalled (attempt $i/$Attempts) - retrying in 5s..."
        Start-Sleep -Seconds 5
    }
    Write-Error "Giving up on '$($Command -join ' ')' after $Attempts attempts."
    exit 1
}

function Get-MachineCdiEntries {
    $entries = podman machine ssh "ls /etc/cdi 2>/dev/null" 2>$null
    if ($LASTEXITCODE -ne 0 -and (Test-Path "/etc/cdi")) {
        # No podman machine (pwsh on native-Linux podman) — check the local filesystem.
        $entries = (Get-ChildItem "/etc/cdi" -Name) -join " "
    }
    return $entries
}

# One-time GPU setup inside the podman machine VM. Called only after the user says yes —
# it installs NVIDIA's container toolkit in the VM (the VM user has passwordless sudo, so
# no prompts). Idempotent. Returns $true when the CDI spec exists at the end.
function Install-MachineGpuSupport {
    $smi = podman machine ssh "ls /usr/lib/wsl/lib/nvidia-smi 2>/dev/null || command -v nvidia-smi" 2>$null
    if (-not $smi) {
        Write-Host ("The podman machine can't see an NVIDIA GPU. Check that the normal NVIDIA " +
            "driver is installed on Windows and the machine uses the WSL2 backend.")
        return $false
    }

    podman machine ssh "command -v nvidia-ctk" 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Installing the NVIDIA container toolkit inside the podman machine..."
        podman machine ssh ("curl -s -L https://nvidia.github.io/libnvidia-container/stable/rpm/nvidia-container-toolkit.repo " +
            "| sudo tee /etc/yum.repos.d/nvidia-container-toolkit.repo >/dev/null && " +
            "sudo dnf install -y nvidia-container-toolkit")
        if ($LASTEXITCODE -ne 0) { Write-Host "Toolkit install failed."; return $false }
    }

    Write-Host "Generating the CDI spec inside the podman machine..."
    podman machine ssh "sudo nvidia-ctk cdi generate --output=/etc/cdi/nvidia.yaml"
    if ($LASTEXITCODE -ne 0) { Write-Host "CDI generation failed."; return $false }

    return ((Get-MachineCdiEntries) -match "nvidia")
}

Step "1/5 Checking for an NVIDIA GPU"
$envFile = Join-Path $PSScriptRoot ".env"
$setInDotEnv = (Test-Path $envFile) -and
    (Select-String -Path $envFile -Pattern '^OLLAMA_GPU_DEVICE=.+' -Quiet)

if ($env:OLLAMA_GPU_DEVICE -or $setInDotEnv) {
    Write-Host "OLLAMA_GPU_DEVICE is set explicitly - using it as-is."
}
elseif ((Get-MachineCdiEntries) -match "nvidia") {
    $env:OLLAMA_GPU_DEVICE = "nvidia.com/gpu=all"
    Write-Host "NVIDIA CDI spec found - the GPU will be attached to Ollama."
}
else {
    Write-Host "Container GPU support isn't set up yet - Ollama would run on the CPU."
    $answer = Read-Host "Set it up now? (installs NVIDIA's container toolkit inside the podman VM) [y/N]"
    if ($answer -match '^[Yy]') {
        if (Install-MachineGpuSupport) {
            $env:OLLAMA_GPU_DEVICE = "nvidia.com/gpu=all"
            Write-Host "GPU setup complete - the GPU will be attached to Ollama."
            Write-Host "Note: 'podman machine rm' + 'init' wipes the VM; you'll be re-offered setup after."
        }
        else {
            Write-Host "Continuing on the CPU."
        }
    }
    else {
        Write-Host "Continuing on the CPU. Run .\up.ps1 again anytime to be re-offered GPU setup."
    }
}

Step "2/5 Downloading the Ollama runtime image (cached after the first run)"
Invoke-Retry -Attempts 3 -TimeoutSec 1200 -Command @("podman", "pull", "docker.io/ollama/ollama:latest")

Step "3/5 Building the assistant and the mock ticketing system"
Invoke-Retry -Attempts 3 -TimeoutSec 1200 -Command @("podman", "compose", "build")

Step "4/5 Starting the containers"
podman compose up -d @ComposeArgs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Step "5/5 Downloading the chat model (a couple of GB on first run, instant after)"
$puller = (podman ps -a --format '{{.Names}}' | Select-String "ollama-pull" | Select-Object -First 1).ToString()
podman logs -f $puller 2>&1
$rc = podman inspect --format '{{.State.ExitCode}}' $puller
if ($rc -ne "0") {
    Write-Error "Model download failed (exit $rc) - see above. Re-run .\up.ps1 to retry."
    exit 1
}

Write-Host ""
Write-Host "Ready." -ForegroundColor Green
Write-Host "  Chat console  -> http://localhost:5080/"
Write-Host "  Ticket board  -> http://localhost:5090/"
