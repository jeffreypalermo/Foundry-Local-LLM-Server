#!/usr/bin/env pwsh
#Requires -Version 7
<#
.SYNOPSIS
    Private build for local workstation use.
    Runs the full build: clean, restore, compile, unit tests, integration tests (stub mode).
    Run this before pushing to verify the build is clean.
.EXAMPLE
    ./privatebuild.ps1
#>

. ./build.ps1
Build
