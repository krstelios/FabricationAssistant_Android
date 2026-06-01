<#
.SYNOPSIS
Full host verification: build the real Android solution, then run the host tests.

.DESCRIPTION
S25-2: test.ps1 alone only runs the net8.0 host test project and can stay green
while the net8.0-android app fails to compile. This script runs build.ps1 first (the
real net8.0-android build), so a broken Android build fails verification, then runs
the host test suite.

GL/shader coverage still requires a connected device with the viewport showing a loaded
model: run-render-queue-test.ps1 drives rendering and asserts both frame timing and
shader COMPILE_STATUS (no compile/link failure).
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'

Write-Host "==> build.ps1 ($Configuration)" -ForegroundColor Cyan
& "$PSScriptRoot/build.ps1" -Configuration $Configuration
if ($LASTEXITCODE -ne 0) { throw "build.ps1 failed with exit code $LASTEXITCODE" }

Write-Host ""
Write-Host "==> test.ps1 ($Configuration)" -ForegroundColor Cyan
& "$PSScriptRoot/test.ps1" -Configuration $Configuration
if ($LASTEXITCODE -ne 0) { throw "test.ps1 failed with exit code $LASTEXITCODE" }

Write-Host ""
Write-Host "Host verification succeeded (Android build + host tests)." -ForegroundColor Green
Write-Host "Reminder: for GL/shader coverage, run tools/run-render-queue-test.ps1 against a connected device showing a loaded model (it drives rendering and asserts shader COMPILE_STATUS)." -ForegroundColor Yellow
