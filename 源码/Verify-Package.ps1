param([Parameter(Mandatory=$true)][string]$Archive,[Parameter(Mandatory=$true)][string]$ExpectedCommit,[string]$ExpectedVersion)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip=[IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $Archive).Path)
try {
    $names=@($zip.Entries.FullName)
    if (@($names|Select-Object -Unique).Count -ne $names.Count) { throw 'Duplicate archive entries.' }
    foreach($name in $names) {
        if ($name -match '(^|/)\.\.(/|$)|\\|^/' -or $name -notmatch '^(FileDock\.exe|README\.md|使用说明\.txt|LICENSE|THIRD-PARTY-NOTICES\.md|build-info\.json|运行时许可/[a-zA-Z0-9.-]+\.TXT|TemperatureService/(?:[a-zA-Z0-9.-]+\.(?:dll|exe|config)|Install\.ps1|第三方许可\.txt))$') { throw "Unexpected package entry: $name" }
    }
    foreach($required in @('FileDock.exe','LICENSE','THIRD-PARTY-NOTICES.md','README.md','使用说明.txt','build-info.json','TemperatureService/Install.ps1','TemperatureService/FileDockTemperature.exe','TemperatureService/LibreHardwareMonitorLib.dll','TemperatureService/第三方许可.txt','运行时许可/microsoft.netcore.app.runtime.win-x64-LICENSE.TXT','运行时许可/microsoft.netcore.app.runtime.win-x64-THIRD-PARTY-NOTICES.TXT','运行时许可/microsoft.windowsdesktop.app.runtime.win-x64-LICENSE.TXT')) {
        if ($required -notin $names) { throw "Missing package entry: $required" }
    }
    $reader=[IO.StreamReader]::new($zip.GetEntry('build-info.json').Open())
    try {$manifest=$reader.ReadToEnd()|ConvertFrom-Json}finally{$reader.Dispose()}
    if ($manifest.commit -ne $ExpectedCommit) { throw 'Package commit mismatch.' }
    if ($ExpectedVersion -and $manifest.version -ne $ExpectedVersion) { throw 'Package version mismatch.' }
    if ($manifest.architecture -ne 'win-x64') { throw 'Package architecture mismatch.' }
    foreach ($document in @('README.md','使用说明.txt')) {
        $reader=[IO.StreamReader]::new($zip.GetEntry($document).Open())
        try {$body=$reader.ReadToEnd()}finally{$reader.Dispose()}
        if (!$body.Contains([string]$manifest.version) -or !$body.Contains('FileDock.exe')) { throw "Package instructions mismatch: $document" }
    }
    if (@($manifest.files).Count -ne $names.Count-1) { throw 'Package manifest coverage mismatch.' }
    if (@($manifest.files.path|Select-Object -Unique).Count -ne @($manifest.files).Count) { throw 'Duplicate manifest entries.' }
    foreach($file in $manifest.files) {
        $entry=$zip.GetEntry($file.path)
        if(!$entry -or $entry.Length -ne $file.bytes){throw "Package size mismatch: $($file.path)"}
        $stream=$entry.Open();$sha=[Security.Cryptography.SHA256]::Create()
        try{$hash=([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-','').ToLowerInvariant()}finally{$sha.Dispose();$stream.Dispose()}
        if($hash -ne $file.sha256){throw "Package checksum mismatch: $($file.path)"}
    }
    Write-Host "Package verified: $($names.Count) files; commit $ExpectedCommit; no personal configuration or test output."
}finally{$zip.Dispose()}
