<#
.SYNOPSIS
Run the host-side Android test suite (plain net8.0).

.DESCRIPTION
This runs ONLY the net8.0 host test project. Most of it is real unit tests over
linked pure logic (gestures, math, import guards, measurement picker, etc.), but a
green run here is NECESSARY BUT NOT SUFFICIENT, for two reasons (S25-1 / S25-2):

  * The GLES renderer cannot load on the net8.0 host, so GlesRendererSourceGuards.cs
    asserts on SOURCE TEXT, not runtime behaviour. It guards specific fixes; it does
    not execute GL or compile a shader.
  * This project compile-links a SUBSET of Android source into a net8.0 assembly. It
    never builds the real net8.0-android app, so it can stay green while the Android
    app fails to compile.

Full verification = build.ps1 (real net8.0-android build) + test.ps1 (this) + the
on-device smoke (run-render-queue-test.ps1, which drives rendering and also asserts
shader COMPILE_STATUS). Use verify.ps1 to chain the build and these host tests in one step.
#>
param(
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$testProject = Join-Path $root "src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj"

dotnet test $testProject -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "dotnet test failed with exit code $LASTEXITCODE" }
