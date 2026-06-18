#!/usr/bin/env pwsh
#Requires -Version 7

$projectName = "FoundryLocalLlmServer"
$base_dir = Resolve-Path .\
$solutionName = Join-Path $base_dir "FoundryLocalLlmServer.sln"
$unitTestProjectPath = Join-Path $base_dir "FoundryLocalLlmServer.UnitTests"
$integrationTestProjectPath = Join-Path $base_dir "FoundryLocalLlmServer.IntegrationTests"
$serverProjectPath = Join-Path $base_dir "FoundryLocalLlmServer.Server"

$projectConfig = $env:BUILD_CONFIGURATION
$version = $env:BUILD_BUILDNUMBER
$verbosity = "quiet"

$build_dir = Join-Path $base_dir "build"
$test_dir = Join-Path $build_dir "test"

if ([string]::IsNullOrEmpty($version)) { $version = "1.0.0" }
if ([string]::IsNullOrEmpty($projectConfig)) { $projectConfig = "Release" }

$script:stubServerProcess = $null

# ── Main Functions ──────────────────────────────────────────────────────────────

Function Init {
    if (Test-Path "build") {
        Remove-Item -Path "build" -Recurse -Force
    }
    New-Item -Path $build_dir -ItemType Directory -Force | Out-Null

    exec {
        & dotnet clean $solutionName -nologo -v $verbosity
    }
    exec {
        & dotnet restore $solutionName -nologo -v $verbosity
    }
}

Function Compile {
    exec {
        & dotnet build $solutionName -nologo --no-restore -v $verbosity `
            --configuration $projectConfig --no-incremental `
            /p:TreatWarningsAsErrors="true" `
            /p:MSBuildTreatAllWarningsAsErrors="true" `
            /p:Version=$version
    }
}

Function UnitTests {
    Push-Location -Path $unitTestProjectPath
    try {
        exec {
            & dotnet test -nologo -v $verbosity --logger:trx `
                --results-directory (Join-Path $test_dir "UnitTests") `
                --no-build --no-restore --configuration $projectConfig `
                --collect:"XPlat Code Coverage"
        }
    }
    finally {
        Pop-Location
    }
}

Function Start-StubServer {
    # Start the server EXE in stub mode on :5537 so ServerFixture.IsRunningAsync()
    # returns true. The collection fixture then reuses this process instead of
    # trying to start its own (which would require Foundry Local + a real GPU).
    $exe = Get-ChildItem -Path (Join-Path $serverProjectPath "bin" $projectConfig) `
        -Recurse -Filter "FoundryLocalLlmServer.Server.exe" -ErrorAction SilentlyContinue |
        Select-Object -First 1

    if (-not $exe) {
        throw "Server EXE not found under $serverProjectPath/bin/$projectConfig — did Compile succeed?"
    }

    Write-Host "Starting stub server: $($exe.FullName)"

    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $exe.FullName
    $psi.WorkingDirectory = $exe.DirectoryName
    $psi.UseShellExecute = $false
    $psi.EnvironmentVariables["ASPNETCORE_URLS"] = "http://localhost:5537"
    $psi.EnvironmentVariables["FoundryLocal__UseStubResponses"] = "true"

    $script:stubServerProcess = [System.Diagnostics.Process]::Start($psi)

    $deadline = (Get-Date).AddSeconds(30)
    $ready = $false
    while ((Get-Date) -lt $deadline) {
        try {
            $r = Invoke-WebRequest -Uri "http://localhost:5537/api/foundry" -TimeoutSec 3 -ErrorAction SilentlyContinue
            if ($r -and $r.StatusCode -lt 500) { $ready = $true; break }
        }
        catch { }
        Start-Sleep -Milliseconds 500
    }

    if (-not $ready) {
        throw "Stub server did not become ready on http://localhost:5537 within 30s"
    }

    Write-Host "Stub server ready."
}

Function Stop-StubServer {
    if ($script:stubServerProcess -and -not $script:stubServerProcess.HasExited) {
        $script:stubServerProcess.Kill($true)
        $script:stubServerProcess.WaitForExit(5000) | Out-Null
    }
    $script:stubServerProcess = $null
}

Function IntegrationTest {
    param([switch]$SkipGpu)

    Push-Location -Path $integrationTestProjectPath
    try {
        $testArgs = @(
            "test", "-nologo", "-v", $verbosity, "--logger:trx",
            "--results-directory", (Join-Path $test_dir "IntegrationTests"),
            "--no-build", "--no-restore", "--configuration", $projectConfig,
            "--collect:XPlat Code Coverage"
        )
        if ($SkipGpu) {
            $testArgs += @("--filter", "Category!=GPU-Required")
        }
        exec { & dotnet @testArgs }
    }
    finally {
        Pop-Location
    }
}

Function Preflight {
    # Verify Foundry Local CLI is installed and required GPU models are downloaded/loaded.
    # Call this before running GPU-dependent integration tests.
    Write-Host "Preflight: checking Foundry Local prerequisites..."

    # 1. foundry CLI must be on PATH
    if (-not (Get-Command foundry -ErrorAction SilentlyContinue)) {
        throw "Preflight FAILED: 'foundry' CLI not found on PATH. Install Foundry Local from https://aka.ms/foundry-local."
    }

    # 2. Service must be reachable
    $serviceOutput = & foundry service start 2>&1 | Out-String
    $urlMatch = [regex]::Match($serviceOutput, 'running on (http://[^\s/]+)')
    if (-not $urlMatch.Success) {
        throw "Preflight FAILED: Foundry Local service did not report a URL. Output: $serviceOutput"
    }
    $foundryUrl = $urlMatch.Groups[1].Value.TrimEnd('/')
    Write-Host "Preflight: Foundry Local running at $foundryUrl"

    # 3. Required GPU models must already be loaded (no auto-download in preflight)
    $requiredModels = @("phi-4-mini", "gemma-4")
    $modelsJson = (Invoke-RestMethod -Uri "$foundryUrl/v1/models" -TimeoutSec 10).data.id
    foreach ($alias in $requiredModels) {
        $gpuLoaded = $modelsJson | Where-Object {
            $id = $_.ToLower()
            $a  = $alias.ToLower()
            ($id -like "$a-*" -or $id -like "$a:*" -or $id -eq $a) -and
            ($id -match 'cuda|trtrtx|-gpu' -and $id -notmatch 'npu')
        }
        if (-not $gpuLoaded) {
            throw "Preflight FAILED: GPU variant of '$alias' is not loaded in Foundry Local. " +
                  "Load it with: foundry model load $alias --device GPU"
        }
        Write-Host "Preflight: GPU model '$alias' is ready."
    }

    Write-Host "Preflight: all prerequisites satisfied."
}

Function Build {
    param([switch]$SkipGpu)

    $sw = [System.Diagnostics.Stopwatch]::StartNew()

    try {
        if (-not $SkipGpu) {
            Preflight
        }
        Init
        Compile
        UnitTests
        Start-StubServer
        IntegrationTest -SkipGpu:$SkipGpu
    }
    finally {
        Stop-StubServer
    }

    $sw.Stop()
    Write-Host "BUILD SUCCEEDED - Build time: $($sw.Elapsed)"
}

# ── Helper Functions ────────────────────────────────────────────────────────────

Function exec([scriptblock]$cmd) {
    & $cmd
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code $LASTEXITCODE"
    }
}
