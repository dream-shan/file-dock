param(
    [Parameter(Mandatory=$true)][string]$Executable,
    [Parameter(Mandatory=$true)][string]$ResultDirectory,
    [switch]$ShortcutsOnly
)
$ErrorActionPreference = 'Stop'
$exe = (Resolve-Path -LiteralPath $Executable).Path
$result = [IO.Path]::GetFullPath($ResultDirectory)
if (Test-Path -LiteralPath $result) { throw 'Use a new desktop test directory.' }
# Requires an unlocked interactive desktop. Uses isolated settings and singleton
# names, and restores the cursor/focus after its external fixture closes.
$cancel = [Threading.EventWaitHandle]::new($false,[Threading.EventResetMode]::AutoReset,'Local\FileDockDemo.DesktopTest.Cancel')
$mode = if($ShortcutsOnly){'--shortcut-test'}else{'--desktop-test'}
$process = Start-Process -FilePath $exe -ArgumentList @($mode, ('"' + $result + '"')) -WindowStyle Hidden -PassThru
try {
    if (!$process.WaitForExit(180000)) {
        [void]$cancel.Set()
        if (!$process.WaitForExit(15000)) { throw 'Desktop test did not finish recovery; process was left running to preserve recovery state.' }
        throw 'Interactive desktop test timed out and requested window recovery.'
    }
    if ($process.ExitCode -ne 0) { throw "Interactive desktop test failed. Inspect $result/failure.txt." }
    $report = Join-Path $result 'desktop-verification.txt'
    $checks = @(Get-Content -LiteralPath $report | Where-Object { $_ -like 'PASS:*' })
    $minimum = if($ShortcutsOnly){7}else{71}
    if ($checks.Count -lt $minimum) { throw 'Interactive desktop test did not finish its scenarios.' }
    if($ShortcutsOnly) { Write-Host "Interactive shortcuts: $($checks.Count) checks passed using real keyboard input and native hotkey dispatch." }
    else { Write-Host "Interactive desktop: $($checks.Count) checks passed using production mouse/window hooks. See report for real-client passes/skips. Drag/capture checks remain separate." }
    Get-Content -LiteralPath $report | Where-Object { $_ -like 'SKIP:*' } | Write-Host
} finally { $process.Dispose(); $cancel.Dispose() }
