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

# S25-3: default to 'Stop' so a failed New-Item / Copy-Item / Remove-Item aborts
# the script instead of silently continuing and shipping a STALE .so (which
# build.ps1's Test-Path guard would then trust as a successful build). Only the
# native cmake calls need stderr tolerance - PowerShell 5.1 wraps the deprecation
# warnings CMake/NDK write to stderr as terminating NativeCommandError records under
# 'Stop' - so each cmake invocation is run via Invoke-NativeTolerant below. Hard
# cmake failures are still caught explicitly via $LASTEXITCODE / throw after each call.
$ErrorActionPreference = 'Stop'

function Invoke-NativeTolerant {
    # Run a native command with ErrorActionPreference temporarily relaxed so its
    # stderr does not raise a terminating NativeCommandError under PS 5.1. The caller
    # inspects $LASTEXITCODE (global; survives the scope restore) to detect failure.
    param([Parameter(Mandatory)][scriptblock]$Command)
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $Command } finally { $ErrorActionPreference = $previous }
}

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

    Invoke-NativeTolerant {
        & cmake -B $buildDir -S $nativeDir -G "Ninja" `
            "-DCMAKE_TOOLCHAIN_FILE=$toolchain" `
            "-DANDROID_ABI=$abi" `
            "-DANDROID_PLATFORM=$AndroidPlatform" `
            "-DCMAKE_BUILD_TYPE=$Configuration"
    }
    if ($LASTEXITCODE -ne 0) { throw "CMake configure failed for $abi (exit $LASTEXITCODE)" }

    Invoke-NativeTolerant { & cmake --build $buildDir --config $Configuration --parallel }
    if ($LASTEXITCODE -ne 0) { throw "Build failed for $abi (exit $LASTEXITCODE)" }

    $produced = Join-Path $buildDir 'libdraco_native.so'
    if (-not (Test-Path $produced)) { throw "Expected output $produced not found" }

    $dest = Join-Path $outDir 'libdraco_native.so'
    Copy-Item $produced $dest -Force
    # S25-3: the copy now runs under 'Stop' (a failed Copy-Item throws), and this
    # assertion additionally rejects a 0-byte / partial result so a broken copy can
    # never ship a stale-or-empty .so past build.ps1's Test-Path existence check.
    $destInfo = Get-Item $dest -ErrorAction SilentlyContinue
    if ($null -eq $destInfo -or $destInfo.Length -le 0) {
        $lenText = if ($null -eq $destInfo) { 'missing' } else { $destInfo.Length }
        throw "Copy to $dest failed or produced an empty file (length=$lenText)."
    }
    Write-Host "Copied to $dest ($($destInfo.Length) bytes)" -ForegroundColor Green
}

Write-Host ""
Write-Host "All ABIs built successfully." -ForegroundColor Green
