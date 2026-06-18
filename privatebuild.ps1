#!/usr/bin/env pwsh
#Requires -Version 7
<#
.SYNOPSIS
    Private build for local workstation use.
    Ensures Foundry Local is running and required GPU models are loaded,
    then runs the full build including GPU-dependent integration tests.
.EXAMPLE
    ./privatebuild.ps1
#>

. ./build.ps1

# ── Setup: Foundry Local prerequisites ──────────────────────────────────────────

Write-Host "Setup: verifying Foundry Local CLI..."
if (-not (Get-Command foundry -ErrorAction SilentlyContinue)) {
    throw "Setup FAILED: 'foundry' CLI not found on PATH. Install from https://aka.ms/foundry-local."
}

Write-Host "Setup: starting Foundry Local service..."
$serviceOutput = & foundry service start 2>&1 | Out-String
$urlMatch = [regex]::Match($serviceOutput, 'running on (http://[^\s/]+)')
if (-not $urlMatch.Success) {
    throw "Setup FAILED: Foundry Local service did not report a URL.`nOutput: $serviceOutput"
}
$foundryUrl = $urlMatch.Groups[1].Value.TrimEnd('/')
Write-Host "Foundry Local running at $foundryUrl"

# Ensure phi-4-mini GPU variant is loaded (download + load if needed)
Write-Host "Setup: checking phi-4-mini GPU model..."
$modelsResponse = Invoke-RestMethod -Uri "$foundryUrl/v1/models" -TimeoutSec 10
$gpuLoaded = $modelsResponse.data.id | Where-Object {
    $lower = $_.ToLower()
    $lower -match 'phi.4.mini' -and ($lower -match 'cuda|trtrtx') -and $lower -notmatch 'npu'
}

if (-not $gpuLoaded) {
    Write-Host "phi-4-mini GPU model not loaded. Attempting to load..."
    & foundry model load phi-4-mini --device GPU --ttl 1800
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Load failed (exit $LASTEXITCODE). Trying download first..."
        & foundry model download phi-4-mini --device GPU
        if ($LASTEXITCODE -ne 0) {
            throw "Setup FAILED: could not download phi-4-mini GPU model."
        }
        & foundry model load phi-4-mini --device GPU --ttl 1800
        if ($LASTEXITCODE -ne 0) {
            throw "Setup FAILED: could not load phi-4-mini GPU model."
        }
    }

    Write-Host "Waiting for phi-4-mini to appear in /v1/models..."
    $deadline = (Get-Date).AddSeconds(60)
    $gpuLoaded = $false
    while ((Get-Date) -lt $deadline) {
        $modelsResponse = Invoke-RestMethod -Uri "$foundryUrl/v1/models" -TimeoutSec 10
        $gpuLoaded = $modelsResponse.data.id | Where-Object {
            $lower = $_.ToLower()
            $lower -match 'phi.4.mini' -and ($lower -match 'cuda|trtrtx') -and $lower -notmatch 'npu'
        }
        if ($gpuLoaded) { break }
        Start-Sleep -Seconds 3
    }

    if (-not $gpuLoaded) {
        throw "Setup FAILED: phi-4-mini GPU model did not appear in /v1/models within 60s."
    }
}

Write-Host "phi-4-mini GPU model is ready: $gpuLoaded"
Write-Host "Setup complete. Running full build..."

# ── Run the full build (including GPU-required tests) ────────────────────────────

Build
