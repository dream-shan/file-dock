param(
    [Parameter(Mandatory=$true)][string]$Executable,
    [Parameter(Mandatory=$true)][string]$ResultDirectory
)
$ErrorActionPreference = 'Stop'
$exe = (Resolve-Path -LiteralPath $Executable).Path
$result = [IO.Path]::GetFullPath($ResultDirectory)
if (Test-Path -LiteralPath $result) { throw 'Use a new test directory; existing data will not be overwritten.' }
New-Item -ItemType Directory -Path $result | Out-Null
$process = Start-Process -FilePath $exe -ArgumentList @('--self-test', ('"' + $result + '"')) -WindowStyle Hidden -PassThru
try {
    if (!$process.WaitForExit(180000)) {
        $process.Kill()
        throw 'Self-test timed out after 180 seconds.'
    }
    if ($process.ExitCode -ne 0) {
        $failure = Join-Path $result 'failure.txt'
        if ($env:GITHUB_ACTIONS -eq 'true' -and (Test-Path -LiteralPath $failure)) {
            # Log only our fixed assertion label; do not upload samples or raw exception paths.
            $label = [regex]::Match([IO.File]::ReadAllText($failure), '(?m)^System.InvalidOperationException: (FAIL: [^\r\n]+)')
            if ($label.Success) { Write-Host $label.Groups[1].Value }
        }
        throw "Self-test failed (exit $($process.ExitCode)). Inspect $result/failure.txt locally."
    }
    $report = Join-Path $result 'verification.txt'
    if (!(Test-Path -LiteralPath $report)) { throw 'Self-test returned no verification report.' }
    $checks = @(Get-Content -LiteralPath $report | Where-Object { $_ -like 'PASS:*' })
    if ($checks.Count -lt 150) { throw 'Self-test did not complete the expected checks.' }
    Write-Host "Self-test: $($checks.Count) checks passed. Desktop capture/drag tests require a separate interactive session."
} finally { $process.Dispose() }
