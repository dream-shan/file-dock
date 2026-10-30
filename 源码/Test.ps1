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
            # Hosted CI uses generated fixtures. Redact runner paths and omit samples/screenshots.
            $diagnostic = [IO.File]::ReadAllText($failure)
            foreach ($path in @($result,$env:GITHUB_WORKSPACE,$env:USERPROFILE)) {
                if ($path) { $diagnostic = $diagnostic.Replace($path,'<test-path>') }
            }
            Write-Host $diagnostic.Substring(0,[Math]::Min(6000,$diagnostic.Length))
        }
        throw "Self-test failed (exit $($process.ExitCode)). Inspect $result/failure.txt locally."
    }
    $report = Join-Path $result 'verification.txt'
    if (!(Test-Path -LiteralPath $report)) { throw 'Self-test returned no verification report.' }
    $checks = @(Get-Content -LiteralPath $report | Where-Object { $_ -like 'PASS:*' })
    if ($checks.Count -lt 258) { throw 'Self-test did not complete the expected checks.' }
    $diagnostics = Join-Path $result 'data\Diagnostics\current.jsonl'
    $events = @(Get-Content -LiteralPath $diagnostics | ForEach-Object { ($_ | ConvertFrom-Json).eventName })
    foreach ($required in @('startup','window-ready','application-exit','message-loop-return','process-exit')) {
        if ($events -notcontains $required) { throw "Missing diagnostic lifecycle event: $required" }
    }
    Write-Host 'Diagnostic process lifecycle checks passed.'
    Write-Host "Self-test: $($checks.Count) checks passed. Desktop capture/drag tests require a separate interactive session."
} finally { $process.Dispose() }
