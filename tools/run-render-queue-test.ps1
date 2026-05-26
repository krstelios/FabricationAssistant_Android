param(
    [string]$AdbPath = "$env:LOCALAPPDATA\Android\Sdk\platform-tools\adb.exe",
    [string]$Package = "com.fabricationassistant.android",
    [int]$SwipeRounds = 10,
    [int]$TapRounds = 5,
    [switch]$KeepExistingLogs
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not (Test-Path $AdbPath)) {
    $AdbPath = "adb"
}

function Invoke-Adb {
    & $script:AdbPath @args
}

function Get-AppPid {
    $pidText = ((Invoke-Adb shell pidof $Package) -join "").Trim()
    if ([string]::IsNullOrWhiteSpace($pidText)) {
        return ""
    }

    return $pidText
}

function Get-DisplaySize {
    $inputDump = Invoke-Adb shell dumpsys input
    foreach ($line in $inputDump) {
        if ($line -match "logicalFrame=\[0, 0, ([0-9]+), ([0-9]+)\]") {
            return [pscustomobject]@{
                Width = [int]$matches[1]
                Height = [int]$matches[2]
            }
        }
    }

    return [pscustomobject]@{ Width = 2800; Height = 1752 }
}

function To-Pixel {
    param(
        [int]$Size,
        [double]$Fraction
    )

    return [int][Math]::Round($Size * $Fraction)
}

$pidText = Get-AppPid
if ([string]::IsNullOrWhiteSpace($pidText)) {
    Write-Host "App is not running; launching $Package."
    Invoke-Adb shell monkey -p $Package 1 | Out-Null
    Start-Sleep -Seconds 2
    $pidText = Get-AppPid
}

if ([string]::IsNullOrWhiteSpace($pidText)) {
    throw "Package $Package is not running and could not be launched."
}

$display = Get-DisplaySize
Write-Host "Device display: $($display.Width)x$($display.Height)"
Write-Host "App PID: $pidText"

if (-not $KeepExistingLogs) {
    Invoke-Adb logcat -c | Out-Null
}

$left = To-Pixel $display.Width 0.23
$right = To-Pixel $display.Width 0.79
$centerX = To-Pixel $display.Width 0.50
$top = To-Pixel $display.Height 0.25
$midY = To-Pixel $display.Height 0.49
$bottom = To-Pixel $display.Height 0.75

$commands = New-Object System.Collections.Generic.List[string]
for ($i = 0; $i -lt $SwipeRounds; $i++) {
    $commands.Add("input swipe $left $midY $right $midY 220")
    $commands.Add("input swipe $right $midY $left $midY 220")
    $commands.Add("input swipe $centerX $top $centerX $bottom 220")
    $commands.Add("input swipe $centerX $bottom $centerX $top 220")
}

$tapPoints = @(
    @(0.32, 0.40),
    @(0.43, 0.43),
    @(0.54, 0.47),
    @(0.64, 0.43),
    @(0.50, 0.57),
    @(0.38, 0.60),
    @(0.61, 0.62)
)

for ($round = 0; $round -lt $TapRounds; $round++) {
    foreach ($point in $tapPoints) {
        $x = To-Pixel $display.Width $point[0]
        $y = To-Pixel $display.Height $point[1]
        $commands.Add("input tap $x $y")
    }
}

for ($i = 0; $i -lt $SwipeRounds; $i++) {
    $x1 = To-Pixel $display.Width 0.27
    $y1 = To-Pixel $display.Height 0.54
    $x2 = To-Pixel $display.Width 0.76
    $y2 = To-Pixel $display.Height 0.37
    $commands.Add("input swipe $x1 $y1 $x2 $y2 160")
    $commands.Add("input swipe $x2 $y2 $x1 $y1 160")
}

Write-Host "Running scripted interaction: $SwipeRounds swipe rounds, $TapRounds tap rounds."
Invoke-Adb shell ($commands -join "; ") | Out-Null
Start-Sleep -Seconds 2

$pidText = Get-AppPid
if ([string]::IsNullOrWhiteSpace($pidText)) {
    throw "App exited during test."
}

$lines = Invoke-Adb logcat -d -v threadtime --pid $pidText

$frameLines = $lines | Select-String -Pattern "FA\.FrameTiming: Render timing" | ForEach-Object { $_.Line }
$frames = foreach ($line in $frameLines) {
    if ($line -match "avg=([0-9.]+)ms, max=([0-9.]+)ms, over16=([0-9]+), over33=([0-9]+), over50=([0-9]+), queue=([0-9.]+)ms, queueCommands=([0-9]+), ssao=([0-9.]+)ms, scene=([0-9.]+)ms") {
        [pscustomobject]@{
            Avg = [double]$matches[1]
            Max = [double]$matches[2]
            Over16 = [int]$matches[3]
            Over33 = [int]$matches[4]
            Over50 = [int]$matches[5]
            Queue = [double]$matches[6]
            Commands = [int]$matches[7]
            Ssao = [double]$matches[8]
            Scene = [double]$matches[9]
        }
    }
}

$queueLines = $lines | Select-String -Pattern "FA\.RenderQueue: GL command timing" | ForEach-Object { $_.Line }
$queues = foreach ($line in $queueLines) {
    if ($line -match "name=([^,]+), run=([0-9.]+)ms, wait=([0-9.]+)ms") {
        [pscustomobject]@{
            Name = $matches[1]
            Run = [double]$matches[2]
            Wait = [double]$matches[3]
        }
    }
}

$slowCount = @($lines | Select-String -Pattern "FA\.Renderer: Slow frame").Count
$skippedCount = @($lines | Select-String -Pattern "Choreographer: Skipped").Count

Write-Host ""
Write-Host "FrameTiming blocks: $(@($frames).Count)"
if (@($frames).Count -gt 0) {
    $frames | Sort-Object Avg -Descending | Select-Object -First 8 | Format-Table -AutoSize
}

Write-Host ""
Write-Host "RenderQueue command timings: $(@($queues).Count)"
if (@($queues).Count -gt 0) {
    Write-Host "Top run times:"
    $queues | Sort-Object Run -Descending | Select-Object -First 12 | Format-Table -AutoSize
    Write-Host "Top wait times:"
    $queues | Sort-Object Wait -Descending | Select-Object -First 12 | Format-Table -AutoSize
}

Write-Host ""
Write-Host "Slow frame log count: $slowCount"
Write-Host "Choreographer skipped-frame log count: $skippedCount"
