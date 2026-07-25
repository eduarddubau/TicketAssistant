#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Checks up.ps1 — on any OS, without starting a single container.

.DESCRIPTION
    up.ps1 only ever runs on Windows in anger, which makes it the easiest file in the repo to
    break from Linux. Three passes, none of which touch a real podman:

      1. Parse    — the whole script is parsed, so syntax errors surface immediately.
      2. Units    — the GPU/keep-alive helpers are lifted out with the AST and called directly
                    against a fake podman (tools/fakes/unit/podman) that reports whatever
                    container state a case needs.
      3. Dry runs — up.ps1 is run end to end against a second fake (tools/fakes/full/podman),
                    asserting the decisions it makes: when it re-creates the Ollama container,
                    what it passes through to compose, and how it ends.

    Run it through tools/test-up-ps1.sh (which supplies PowerShell in a container), or directly
    with `pwsh tools/test-up.ps1` wherever pwsh is installed.

.PARAMETER Analyze
    Also run PSScriptAnalyzer. Installs the module on first use, so it needs network access.
#>
[CmdletBinding()]
param([switch]$Analyze)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$upPs1 = Join-Path $repoRoot 'up.ps1'
$unitFake = Join-Path $PSScriptRoot 'fakes/unit'
$fullFake = Join-Path $PSScriptRoot 'fakes/full'
$originalPath = $env:PATH

$script:failed = 0
function Check([string]$Name, $Expected, $Actual) {
    if ($Expected -eq $Actual) { Write-Host "  PASS  $Name" }
    else { Write-Host "  FAIL  $Name  (expected '$Expected', got '$Actual')"; $script:failed++ }
}
function CheckMatch([string]$Name, [string]$Text, [string]$Pattern, [bool]$ShouldMatch = $true) {
    if (($Text -match $Pattern) -eq $ShouldMatch) { Write-Host "  PASS  $Name" }
    else {
        $what = if ($ShouldMatch) { "expected to match" } else { "expected NOT to match" }
        Write-Host "  FAIL  $Name  ($what '$Pattern')"
        $script:failed++
    }
}

# --- 1. Parse -------------------------------------------------------------------------
Write-Host "Parsing up.ps1"
$parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($upPs1, [ref]$null, [ref]$parseErrors)
if ($parseErrors) {
    $parseErrors | ForEach-Object { Write-Host "  FAIL  line $($_.Extent.StartLineNumber): $($_.Message)" }
    exit 1   # nothing below can run against a script that doesn't parse
}
Write-Host "  PASS  no syntax errors"

# --- 2. Static analysis (opt-in) ------------------------------------------------------
if ($Analyze) {
    Write-Host "PSScriptAnalyzer"
    # Pinned: the current release refuses to load on PowerShell older than 7.4.6, which is
    # what the container image ships.
    if (-not (Get-Module -ListAvailable PSScriptAnalyzer)) {
        Install-Module PSScriptAnalyzer -RequiredVersion 1.22.0 -Force -Scope CurrentUser | Out-Null
    }
    # Write-Host is exactly right for a script whose whole job is talking to the user, and the
    # em dashes in the comments are deliberate — neither rule says anything useful here.
    $findings = Invoke-ScriptAnalyzer -Path $upPs1 -Severity Error, Warning `
        -ExcludeRule PSAvoidUsingWriteHost, PSUseBOMForUnicodeEncodedFile, PSUseSingularNouns
    if ($findings) {
        $findings | ForEach-Object { Write-Host "  FAIL  line $($_.Line) $($_.RuleName): $($_.Message)"; $script:failed++ }
    }
    else { Write-Host "  PASS  no findings" }
}

# --- 2. Units -------------------------------------------------------------------------
# The helpers are dot-sourced from a scratch directory rather than from up.ps1 itself:
# running up.ps1 here would start the stack, and Get-DotEnv reads .env relative to the
# $PSScriptRoot of the file it was defined in — which is how the fixtures below get picked up.
$work = Join-Path ([System.IO.Path]::GetTempPath()) ("up-check-" + [guid]::NewGuid())
New-Item -ItemType Directory -Path $work | Out-Null
try {
    $helpers = $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $false) |
        ForEach-Object { $_.Extent.Text }
    Set-Content -Path (Join-Path $work 'helpers.ps1') -Value ($helpers -join "`n`n")
    . (Join-Path $work 'helpers.ps1')

    $env:PATH = $unitFake + [System.IO.Path]::PathSeparator + $originalPath

    Write-Host "Get-DotEnv, ordinary .env"
    Set-Content -Path (Join-Path $work '.env') -Value @(
        '# Copy to .env and override as needed.',
        'LLM_PROVIDER=Ollama',
        'OLLAMA_MODELS=qwen2.5:3b qwen2.5:1.5b',
        'OLLAMA_GPU_DEVICE=nvidia.com/gpu=all'
    )
    Check "reads a value"                 'nvidia.com/gpu=all'      (Get-DotEnv 'OLLAMA_GPU_DEVICE')
    Check "keeps spaces in a list value"  'qwen2.5:3b qwen2.5:1.5b' (Get-DotEnv 'OLLAMA_MODELS')
    Check "absent variable is empty"      ''                        (Get-DotEnv 'OLLAMA_KEEP_ALIVE')

    Write-Host "Get-DotEnv, quoted and padded .env"
    Set-Content -Path (Join-Path $work '.env') -Value @(
        'OLLAMA_GPU_DEVICE="nvidia.com/gpu=all"  ',
        "OLLAMA_KEEP_ALIVE='10m'"
    )
    Check "strips quotes and padding" 'nvidia.com/gpu=all' (Get-DotEnv 'OLLAMA_GPU_DEVICE')
    Check "strips single quotes"      '10m'                (Get-DotEnv 'OLLAMA_KEEP_ALIVE')

    Write-Host "Get-DotEnv, no .env at all"
    Remove-Item (Join-Path $work '.env') -Force
    Check "returns empty rather than throwing" '' (Get-DotEnv 'OLLAMA_GPU_DEVICE')

    # CDI expands nvidia.com/gpu=all into the individual device nodes, which is what a real
    # `podman inspect` reports back — so that, not the request, is what the fake returns.
    $gpuDevices = '/dev/nvidia-uvm /dev/nvidiactl /dev/nvidia0 '
    $cpuDevices = '/dev/null '

    function Scenario([string]$Containers, [string]$Devices, [string]$ContainerKeepAlive,
                      [string]$WantGpu, [string]$WantKeepAlive) {
        $env:FAKE_CONTAINERS = $Containers
        $env:FAKE_DEVICES = $Devices
        $env:FAKE_ENV = "OLLAMA_HOST=0.0.0.0:11434`nOLLAMA_KEEP_ALIVE=$ContainerKeepAlive"
        $env:OLLAMA_GPU_DEVICE = $WantGpu
        $env:OLLAMA_KEEP_ALIVE = $WantKeepAlive
        return Test-OllamaStale
    }

    Write-Host "Test-OllamaStale"
    Check "no container yet -> nothing to re-create" $false `
        (Scenario '' '' '' 'nvidia.com/gpu=all' '')
    Check "GPU wanted, GPU attached -> up to date" $false `
        (Scenario 'ticketassistant_ollama_1' $gpuDevices '-1' 'nvidia.com/gpu=all' '')
    Check "GPU wanted, container on CPU -> stale" $true `
        (Scenario 'ticketassistant_ollama_1' $cpuDevices '-1' 'nvidia.com/gpu=all' '')
    Check "CPU wanted, container has GPU -> stale" $true `
        (Scenario 'ticketassistant_ollama_1' $gpuDevices '-1' '' '')
    Check "explicit /dev/null counts as CPU -> stale" $true `
        (Scenario 'ticketassistant_ollama_1' $gpuDevices '-1' '/dev/null' '')
    Check "container would unload when idle -> stale" $true `
        (Scenario 'ticketassistant_ollama_1' $gpuDevices '5m' 'nvidia.com/gpu=all' '')
    Check "explicit keep-alive the container already has -> up to date" $false `
        (Scenario 'ticketassistant_ollama_1' $gpuDevices '30m' 'nvidia.com/gpu=all' '30m')
    Check "docker-compose style container name" $false `
        (Scenario 'ticketassistant-ollama-1' $gpuDevices '-1' 'nvidia.com/gpu=all' '')
    Check "unrelated containers are ignored" $false `
        (Scenario "jellyfin`nticketassistant_ollama_1" $gpuDevices '-1' 'nvidia.com/gpu=all' '')
}
finally {
    $env:PATH = $originalPath
    Remove-Item -Recurse -Force $work -ErrorAction Ignore
}

# --- 3. Dry runs ----------------------------------------------------------------------
# up.ps1 start to finish against the second fake. OLLAMA_GPU_DEVICE is set explicitly so the
# run never depends on the .env of whoever is running this, or on a real GPU being present.
function Invoke-DryRun {
    param([hashtable]$Fake = @{}, [string[]]$PassThrough = @())
    $env:PATH = $fullFake + [System.IO.Path]::PathSeparator + $originalPath
    $env:OLLAMA_GPU_DEVICE = 'nvidia.com/gpu=all'
    $env:OLLAMA_KEEP_ALIVE = ''
    foreach ($name in 'FAKE_DEVICES', 'FAKE_KEEPALIVE', 'FAKE_PROCESSOR', 'FAKE_PULL_EXIT') {
        Set-Item "env:$name" -Value ([string]$Fake[$name])
    }
    try {
        # Empty stdin so up.ps1's "you may close this window" hold sees a redirected input and
        # returns instead of idling forever.
        $output = '' | & pwsh -NoProfile -File $upPs1 @PassThrough 2>&1 | Out-String
        return [pscustomobject]@{ Output = $output; ExitCode = $LASTEXITCODE }
    }
    finally { $env:PATH = $originalPath }
}

Write-Host "Dry run: the Ollama container already matches"
$run = Invoke-DryRun -PassThrough @('--build')
CheckMatch "starts the stack, passing extra args through" $run.Output '\[fake\] compose up args: -d --build'
CheckMatch "does not re-create anything"                  $run.Output 'force-recreate' $false
CheckMatch "reports the model on the GPU"                  $run.Output 'loaded on the GPU'
CheckMatch "ends with success"                             $run.Output 'Success - everything is up'
Check      "exit code"                                     0 $run.ExitCode

Write-Host "Dry run: the container was created without the GPU"
$run = Invoke-DryRun -Fake @{ FAKE_DEVICES = '/dev/null ' }
CheckMatch "says why it is re-creating" $run.Output 'different GPU/keep-alive settings'
CheckMatch "re-creates the stack"       $run.Output '\[fake\] compose up args: -d --force-recreate'
Check      "exit code"                  0 $run.ExitCode

Write-Host "Dry run: the container would unload the model when idle"
$run = Invoke-DryRun -Fake @{ FAKE_KEEPALIVE = '5m' }
CheckMatch "re-creates the stack" $run.Output '\[fake\] compose up args: -d --force-recreate'
Check      "exit code"            0 $run.ExitCode

Write-Host "Dry run: Ollama ended up on the CPU anyway"
$run = Invoke-DryRun -Fake @{ FAKE_PROCESSOR = '100% CPU' }
CheckMatch "says so instead of claiming the GPU" $run.Output 'loaded on the CPU'
CheckMatch "points at the README"                $run.Output 'GPU acceleration'
Check      "exit code"                           0 $run.ExitCode

Write-Host "Dry run: the model download fails"
$run = Invoke-DryRun -Fake @{ FAKE_PULL_EXIT = '1' }
CheckMatch "reports the failure"          $run.Output 'Startup failed'
CheckMatch "skips the final GPU check"    $run.Output '6/6' $false
Check      "exit code"                    1 $run.ExitCode

Write-Host ""
if ($script:failed -gt 0) { Write-Host "$script:failed check(s) failed"; exit 1 }
Write-Host "All checks passed"
