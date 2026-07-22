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

podman compose up -d @ComposeArgs
exit $LASTEXITCODE
