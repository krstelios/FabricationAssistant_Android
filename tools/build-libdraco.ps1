<#
.SYNOPSIS
Build libdraco_native.so for arm64-v8a and x86_64 from Android/deps/draco
via the Android NDK CMake toolchain. Outputs to Android/deps/prebuilt/<ABI>/.

.NOTES
Requires Android NDK + CMake. Auto-discovers the latest NDK under
$env:LOCALAPPDATA\Android\Sdk\ndk if -NdkPath is not specified.

Plan 2B Task 3.
#>
[CmdletBinding()]
param(
    [string]$NdkPath,
    [string[]]$Abis = @('arm64-v8a','x86_64'),
    [string]$AndroidPlatform = 'android-24',
    [ValidateSet('Debug','Release')]
    [string]$Configuration = 'Release'
)

# Use 'Continue' rather than 'Stop' so that the deprecation-warning lines
# CMake/NDK write to stderr (wrapped by PowerShell 5.1 as NativeCommandError
# records) don't terminate the script. Hard failures are caught explicitly via
# $LASTEXITCODE / throw at each step.
$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $PSScriptRoot
$nativeDir = Join-Path $root 'src/FabricationAssistant.Draco.Android/native'
$prebuiltDir = Join-Path $root 'deps/prebuilt'

if (-not $NdkPath) {
    $ndkRoot = Join-Path $env:LOCALAPPDATA 'Android\Sdk\ndk'
    if (-not (Test-Path $ndkRoot)) { throw "NDK root not found at $ndkRoot. Install via Android Studio's SDK Manager or the cmdline-tools sdkmanager (e.g. ``sdkmanager 'ndk;26.3.11579264'``)." }
    $NdkPath = (Get-ChildItem $ndkRoot -Directory | Sort-Object Name -Descending | Select-Object -First 1).FullName
    Write-Host "Auto-selected NDK: $NdkPath"
}

$toolchain = Join-Path $NdkPath 'build/cmake/android.toolchain.cmake'
if (-not (Test-Path $toolchain)) { throw "Toolchain file not found at $toolchain" }

# Prefer the SDK-bundled CMake + Ninja when present (matches the toolchain's
# expectations); fall back to whatever is on PATH otherwise.
$sdkCmakeRoot = Join-Path $env:LOCALAPPDATA 'Android\Sdk\cmake'
if (Test-Path $sdkCmakeRoot) {
    $cmakeBin = (Get-ChildItem $sdkCmakeRoot -Directory | Sort-Object Name -Descending | Select-Object -First 1).FullName + '\bin'
    $env:PATH = "$cmakeBin;$env:PATH"
}

foreach ($abi in $Abis) {
    $buildDir = Join-Path $nativeDir "build-$abi"
    $outDir = Join-Path $prebuiltDir $abi
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null

    Write-Host ""
    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host "Building libdraco_native.so for $abi"
    Write-Host "========================================" -ForegroundColor Cyan

    if (Test-Path $buildDir) { Remove-Item -Recurse -Force $buildDir }

    & cmake -B $buildDir -S $nativeDir -G "Ninja" `
        "-DCMAKE_TOOLCHAIN_FILE=$toolchain" `
        "-DANDROID_ABI=$abi" `
        "-DANDROID_PLATFORM=$AndroidPlatform" `
        "-DCMAKE_BUILD_TYPE=$Configuration"
    if ($LASTEXITCODE -ne 0) { throw "CMake configure failed for $abi (exit $LASTEXITCODE)" }

    & cmake --build $buildDir --config $Configuration --parallel
    if ($LASTEXITCODE -ne 0) { throw "Build failed for $abi (exit $LASTEXITCODE)" }

    $produced = Join-Path $buildDir 'libdraco_native.so'
    if (-not (Test-Path $produced)) { throw "Expected output $produced not found" }
    Copy-Item $produced (Join-Path $outDir 'libdraco_native.so') -Force
    Write-Host "Copied to $outDir\libdraco_native.so" -ForegroundColor Green
}

Write-Host ""
Write-Host "All ABIs built successfully." -ForegroundColor Green
