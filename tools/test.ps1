param(
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$testProject = Join-Path $root "src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj"

dotnet test $testProject -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "dotnet test failed with exit code $LASTEXITCODE" }
