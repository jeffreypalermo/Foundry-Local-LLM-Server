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
    # Only run tests that are safe without a real GPU / Foundry Local runtime:
    #   OpenAiCompatibilityTests       — uses WebApplicationFactory + stub mode
    #   FoundryServiceHelperAliasTests — pure logic, no server required
    #
    # Category!=GPU-Required is NOT used here: a test with multiple Category traits
    # (Category=Integration AND Category=GPU-Required) would still be included
    # because Category=Integration satisfies the != condition.
    Push-Location -Path $integrationTestProjectPath
    try {
        exec {
            & dotnet test -nologo -v $verbosity --logger:trx `
                --results-directory (Join-Path $test_dir "IntegrationTests") `
                --no-build --no-restore --configuration $projectConfig `
                --filter "FullyQualifiedName~OpenAiCompatibilityTests|FullyQualifiedName~FoundryServiceHelperAliasTests" `
                --collect:"XPlat Code Coverage"
        }
    }
    finally {
        Pop-Location
    }
}

Function Build {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()

    try {
        Init
        Compile
        UnitTests
        Start-StubServer
        IntegrationTest
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
