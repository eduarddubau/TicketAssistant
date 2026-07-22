# Windows counterpart of up.sh: starts the stack, attaching the host's NVIDIA GPU to
# Ollama when it's available, falling back to CPU when it isn't.
#
#   .\up.ps1             # up -d, GPU auto-detected
#   .\up.ps1 --build     # extra args are passed through to `podman compose up -d`
#
# On Windows, podman runs containers inside a Linux VM (`podman machine`), so the CDI spec
# that exposes the GPU lives *inside that VM* — detection asks the machine, not Windows.
# An explicit OLLAMA_GPU_DEVICE in the environment or .env always wins.
param([Parameter(ValueFromRemainingArguments = $true)][string[]]$ComposeArgs)

Set-Location $PSScriptRoot

# --- GPU auto-detect ---------------------------------------------------------------------

$envFile = Join-Path $PSScriptRoot ".env"
$setInDotEnv = (Test-Path $envFile) -and
    (Select-String -Path $envFile -Pattern '^OLLAMA_GPU_DEVICE=.+' -Quiet)

if (-not $env:OLLAMA_GPU_DEVICE -and -not $setInDotEnv) {
    # Look for an NVIDIA CDI spec: inside the podman machine when there is one (Windows /
    # macOS), or on the local filesystem when podman runs natively (PowerShell on Linux).
    $cdiEntries = podman machine ssh "ls /etc/cdi 2>/dev/null" 2>$null
    if ($LASTEXITCODE -ne 0 -and (Test-Path "/etc/cdi")) {
        $cdiEntries = (Get-ChildItem "/etc/cdi" -Name) -join " "
    }

    if ($cdiEntries -match "nvidia") {
        $env:OLLAMA_GPU_DEVICE = "nvidia.com/gpu=all"
        Write-Host "NVIDIA CDI spec found - attaching GPU to Ollama (set OLLAMA_GPU_DEVICE in .env to override)."
    }
    else {
        Write-Host "No NVIDIA CDI spec found - Ollama will run on the CPU."
    }
}

# --- Downloads with retry ----------------------------------------------------------------
# Registry pulls occasionally stall indefinitely. Each download step runs under a timeout
# and is retried a few times, so a transient blip doesn't require any debugging — worst
# case the script fails loudly after several attempts instead of hanging silently.

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

# Pull the one big external image up front — the pull most likely to stall; completed
# layers are kept between attempts.
Invoke-Retry -Attempts 3 -TimeoutSec 1200 -Command @("podman", "pull", "docker.io/ollama/ollama:latest")

# Build the two services (downloads the dotnet base images on a fresh host).
Invoke-Retry -Attempts 3 -TimeoutSec 1200 -Command @("podman", "compose", "build")

podman compose up -d @ComposeArgs
exit $LASTEXITCODE
