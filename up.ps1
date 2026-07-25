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

# Every ending — success or any failure — goes through here: print a clear banner (the
# error text itself is in the output just above) so the outcome is obvious, and tell the
# user the window is safe to close. Non-interactive runs skip that note.
function Finish([int]$Code) {
    Write-Host ""
    if ($Code -eq 0) {
        Write-Host "Success - everything is up and ready." -ForegroundColor Green
        Write-Host "  Console       -> http://localhost:4200/   (the app - start here)"
        Write-Host "  Ticket board  -> http://localhost:5090/"
    }
    else {
        Write-Host "Startup failed (exit $Code) - the error is in the output above." -ForegroundColor Red
    }
    if (-not [Console]::IsInputRedirected) {
        # The terminal window closes the moment this script exits, taking the outcome
        # with it — so don't exit: idle until the user closes the window themselves
        # (Ctrl+C also returns to the shell when run from one).
        Write-Host "You may close this window (Ctrl+C returns to the shell)."
        while ($true) { Start-Sleep -Seconds 3600 }
    }
    exit $Code
}

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
    Finish 1
}

# Reads a variable out of .env. Compose reads that file itself, but this script has to know
# what was asked for to check the running container against it.
function Get-DotEnv([string]$Name) {
    $file = Join-Path $PSScriptRoot ".env"
    if (-not (Test-Path $file)) { return "" }
    $line = Select-String -Path $file -Pattern "^$Name=(.*)$" | Select-Object -First 1
    if (-not $line) { return "" }
    return $line.Matches[0].Groups[1].Value.Trim().Trim('"', "'")
}

function Get-OllamaContainer {
    $name = podman ps -a --format '{{.Names}}' | Select-String '[-_]ollama[-_]1$' | Select-Object -First 1
    if ($name) { return $name.ToString() }
    return ""
}

# podman-compose never re-creates an existing container: `up -d` just starts what's already
# there, devices and environment included. So an ollama container built before the GPU was
# set up would quietly stay on the CPU — and keep whatever idle-unload timer it was created
# with — however carefully this script detects a GPU. Compare what the existing container
# has against what's being asked for, so step 4 can re-create it when they differ.
function Test-OllamaStale {
    $name = Get-OllamaContainer
    if (-not $name) { return $false }   # nothing there yet — compose will create it with today's settings

    # CDI expands nvidia.com/gpu=all into the individual /dev/nvidia* nodes, so the device list
    # is compared as "has NVIDIA nodes or not" rather than against the request verbatim.
    $devices = podman inspect $name --format '{{range .HostConfig.Devices}}{{.PathOnHost}} {{end}}' 2>$null
    $hasGpu = "$devices" -match "nvidia"
    $wantGpu = $env:OLLAMA_GPU_DEVICE -and $env:OLLAMA_GPU_DEVICE -ne "/dev/null"

    $keepAlive = (podman inspect $name --format '{{range .Config.Env}}{{println .}}{{end}}' 2>$null |
        Select-String '^OLLAMA_KEEP_ALIVE=(.*)$' | Select-Object -First 1)
    $keepAlive = if ($keepAlive) { $keepAlive.Matches[0].Groups[1].Value } else { "" }
    $wantKeepAlive = if ($env:OLLAMA_KEEP_ALIVE) { $env:OLLAMA_KEEP_ALIVE } else { Get-DotEnv "OLLAMA_KEEP_ALIVE" }
    if (-not $wantKeepAlive) { $wantKeepAlive = "-1" }

    return ($hasGpu -ne $wantGpu) -or ($keepAlive -ne $wantKeepAlive)
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

Step "1/6 Checking for an NVIDIA GPU"
# An explicit setting always wins; the .env one is pulled into the environment so the rest
# of the script knows which device the containers are getting.
if (-not $env:OLLAMA_GPU_DEVICE) { $env:OLLAMA_GPU_DEVICE = Get-DotEnv "OLLAMA_GPU_DEVICE" }

if ($env:OLLAMA_GPU_DEVICE) {
    Write-Host "OLLAMA_GPU_DEVICE is set explicitly ($env:OLLAMA_GPU_DEVICE) - using it as-is."
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

Step "2/6 Downloading the Ollama runtime image (cached after the first run)"
Invoke-Retry -Attempts 3 -TimeoutSec 1200 -Command @("podman", "pull", "docker.io/ollama/ollama:latest")

Step "3/6 Building the assistant and the mock ticketing system"
Invoke-Retry -Attempts 3 -TimeoutSec 1200 -Command @("podman", "compose", "build")

Step "4/6 Starting the containers"
# --force-recreate covers the whole stack (podman-compose doesn't honour a per-service filter
# here), which is fine: everything is stateless — Ollama's models live in a volume, and the
# model itself is re-loaded by the warm-up in step 5.
$upArgs = @()
if (Test-OllamaStale) {
    Write-Host "The existing Ollama container has different GPU/keep-alive settings - re-creating it."
    $upArgs += "--force-recreate"
}
if ($ComposeArgs) { $upArgs += $ComposeArgs }
# podman-compose occasionally trips over recreating an exited one-shot container
# ("no container ... found", exit 125); a single retry settles it.
podman compose up -d @upArgs
if ($LASTEXITCODE -ne 0) {
    Write-Host "compose up hit a transient error - retrying once..."
    Start-Sleep -Seconds 2
    podman compose up -d @upArgs
    if ($LASTEXITCODE -ne 0) { Finish $LASTEXITCODE }
}

Step "5/6 Downloading the chat model (a couple of GB on first run, instant after)"
$puller = (podman ps -a --format '{{.Names}}' | Select-String "ollama-pull" | Select-Object -First 1).ToString()
podman logs -f $puller 2>&1
$rc = podman inspect --format '{{.State.ExitCode}}' $puller
if ($rc -ne "0") {
    Write-Error "Model download failed (exit $rc) - see above. Re-run .\up.ps1 to retry."
    Finish 1
}

Step "6/6 Checking what Ollama ended up running"
# What actually happened, rather than what was asked for: PROCESSOR says GPU or CPU, and
# UNTIL "Forever" confirms the model stays loaded instead of being unloaded when idle.
$ollama = Get-OllamaContainer
if ($ollama) {
    $ps = podman exec $ollama ollama ps 2>&1
    $ps | Write-Host
    if ("$ps" -match "GPU") {
        Write-Host "The model is loaded on the GPU and stays loaded - the first message is instant."
    }
    else {
        Write-Host "The model is loaded on the CPU (replies will be slower). See README ""GPU acceleration""."
    }
}

Finish 0
