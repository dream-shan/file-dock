$ErrorActionPreference='Stop'
[Console]::OutputEncoding=[Text.Encoding]::UTF8
$repo=Split-Path $PSScriptRoot -Parent
Push-Location $repo
try {
    $files=@(& git -c core.quotepath=false ls-files)
    if($LASTEXITCODE -ne 0){throw 'Cannot list tracked files.'}
    $issues=@()
    foreach($name in $files) {
        if($name -match '(^|/)(Data|更新备份|work|artifacts|bin|obj|test-results|\.vs)(/|$)|(^|/)(\.env($|\.)|id_rsa|credentials)|\.(exe|dll|pdb|zip|pfx|p12)$') {
            $issues+="$name : disallowed tracked file"; continue
        }
        if($name -match '\.(ico|png)$'){continue}
        $body=[IO.File]::ReadAllText((Join-Path $repo $name))
        if($body -match '(?:gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{40,}|AKIA[0-9A-Z]{16}|-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----)') {
            $issues+="$name : possible credential (value omitted)"
        }
        if($body -match '(?i)[A-Z]:[\\/]+Users[\\/]+(?!Public(?:[\\/]|$)|Default(?:[\\/]|$))[^\\/\s"<>]+[\\/]') {
            $issues+="$name : literal personal home path (value omitted)"
        }
    }
    if($issues.Count){throw ($issues -join "`n")}
    Write-Host "Source boundary check passed for $($files.Count) tracked files. This is a targeted check, not proof that every kind of secret is absent."
}finally{Pop-Location}
