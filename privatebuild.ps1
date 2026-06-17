#Requires -Version 7
<#
.SYNOPSIS
    Private build script for local workstation use.
    Restores, builds the full solution, and runs unit tests.
    Run this before pushing to verify the build is clean.
.EXAMPLE
    ./privatebuild.ps1
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = $PSScriptRoot
$sln = Join-Path $repoRoot 'FoundryLocalLlmServer.sln'
$unitTestProj = Join-Path $repoRoot 'FoundryLocalLlmServer.UnitTests/FoundryLocalLlmServer.UnitTests.csproj'
$testResults = Join-Path $repoRoot 'build/test'

Write-Host "`n=== Private Build ===" -ForegroundColor Cyan

# Restore
Write-Host "`n--- Restore ---" -ForegroundColor Yellow
dotnet restore $sln
if ($LASTEXITCODE -ne 0) { throw "Restore failed" }

# Build
Write-Host "`n--- Build ---" -ForegroundColor Yellow
dotnet build $sln --no-restore --configuration Debug
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

# Unit tests
Write-Host "`n--- Unit Tests ---" -ForegroundColor Yellow
New-Item -ItemType Directory -Force -Path $testResults | Out-Null
dotnet test $unitTestProj --no-build --configuration Debug `
    --logger "trx;LogFileName=unit-tests.trx" `
    --results-directory $testResults `
    --logger "console;verbosity=normal"
if ($LASTEXITCODE -ne 0) { throw "Unit tests failed" }

Write-Host "`n=== Private Build PASSED ===" -ForegroundColor Green
