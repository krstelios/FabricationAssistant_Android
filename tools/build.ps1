<#
.SYNOPSIS
Build the Android solution.

.NOTES
This script ONLY drives the Android solution. The root build.ps1 drives the
Windows/desktop solution and is unaffected by this script.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')]
    [string]$Configuration = 'Debug',
    [switch]$Restore = $true,
    [switch]$Clean
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$sln = Join-Path $root 'FabricationAssistant.Android.sln'

if (-not (Test-Path $sln)) {
    throw "Solution not found: $sln"
}

# Ensure ANDROID_HOME is set for this process. The .NET Android workload's
# Xamarin.Android.Tooling.targets requires it (or AndroidSdkDirectory) to be
# resolvable at build time. We fall back to the canonical Windows install path.
if (-not $env:ANDROID_HOME) {
    $candidate = Join-Path $env:LOCALAPPDATA 'Android\Sdk'
    if (Test-Path $candidate) {
        $env:ANDROID_HOME = $candidate
        $env:ANDROID_SDK_ROOT = $candidate
        Write-Host "ANDROID_HOME not set; using fallback: $candidate"
    } else {
        Write-Warning "ANDROID_HOME is not set and the fallback path '$candidate' does not exist. The Android SDK must be installed (via Android Studio's SDK Manager) before this build can succeed."
    }
}

# If a JDK 17 install is available under the user's local apps, prefer it.
# The .NET Android workload 34.0.154's ValidateJavaVersion task cannot parse
# the version string format JDK 21 produces (e.g. "21+35-LTS-2513"), which
# produces XA0033 warnings on every build. JDK 17 (17.0.x format) parses
# cleanly. This is a temporary workaround until the workload updates.
if (-not $env:JAVA_HOME -or $env:JAVA_HOME -like "*jdk-21*" -or $env:JAVA_HOME -like "*jdk-22*") {
    $jdk17Root = Join-Path $env:LOCALAPPDATA 'JDK17'
    if (Test-Path $jdk17Root) {
        $jdk17 = Get-ChildItem $jdk17Root -Directory -Filter 'jdk-17*' -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($jdk17) {
            $env:JAVA_HOME = $jdk17.FullName
            Write-Host "JAVA_HOME redirected to JDK 17 install: $($jdk17.FullName)"
        }
    }
}

if ($Clean) {
    Write-Host "Cleaning $sln ($Configuration)..."
    dotnet clean $sln -c $Configuration | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet clean failed with exit code $LASTEXITCODE" }
}

# Plan 2B: Build libdraco_native.so for both supported ABIs if not already
# present in deps/prebuilt/. The .so files are bundled into the APK via
# <AndroidNativeLibrary> entries in App.Android.csproj.
$dracoArm64 = Join-Path $root 'deps/prebuilt/arm64-v8a/libdraco_native.so'
$dracoX64 = Join-Path $root 'deps/prebuilt/x86_64/libdraco_native.so'
if (-not (Test-Path $dracoArm64) -or -not (Test-Path $dracoX64)) {
    Write-Host "libdraco_native.so not present in deps/prebuilt; building via build-libdraco.ps1..."
    & "$PSScriptRoot/build-libdraco.ps1"
    if ($LASTEXITCODE -ne 0) { throw "build-libdraco.ps1 failed with exit code $LASTEXITCODE" }
}

if ($Restore) {
    Write-Host "Restoring $sln..."
    dotnet restore $sln | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed with exit code $LASTEXITCODE" }
}

Write-Host "Building $sln ($Configuration)..."
dotnet build $sln -c $Configuration --no-restore | Out-Host
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE" }

Write-Host "Build succeeded."
