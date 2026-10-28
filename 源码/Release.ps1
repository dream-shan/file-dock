param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path $PSScriptRoot -Parent
Push-Location $repo
try {
    $revision = & git rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Release requires a Git checkout.' }
    $dirty = & git status --porcelain --untracked-files=normal
    if ($LASTEXITCODE -ne 0 -or $dirty) { throw 'Commit the intended source changes before packaging; checkout must be clean.' }
    & (Join-Path $PSScriptRoot 'Verify-Repository.ps1')
    [xml]$project = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'FileDockDemo.csproj')
    $version = [string]$project.Project.PropertyGroup.Version
    if ($env:GITHUB_REF -like 'refs/tags/*' -and $env:GITHUB_REF -ne "refs/tags/v$version") { throw 'Release tag does not match the project version.' }
    if (!$OutputDirectory) { $OutputDirectory = Join-Path $repo "artifacts\$version" }
    $output = [IO.Path]::GetFullPath($OutputDirectory)
    if (Test-Path -LiteralPath $output) { throw 'Use a new release directory. Existing outputs will not be overwritten.' }
    New-Item -ItemType Directory -Path $output | Out-Null
    $build = Join-Path $output 'build'
    & (Join-Path $PSScriptRoot 'Build.ps1') -OutputDirectory $build
    & (Join-Path $PSScriptRoot 'Test.ps1') -Executable (Join-Path $build 'FileDockDemo.exe') -ResultDirectory (Join-Path $output 'checks')
    $stage = Join-Path $output 'package'
    New-Item -ItemType Directory -Path $stage | Out-Null
    Copy-Item -LiteralPath (Join-Path $build 'FileDockDemo.exe') -Destination $stage
    foreach ($name in @('README.md','使用说明.txt','LICENSE','THIRD-PARTY-NOTICES.md')) {
        $source = Join-Path $repo $name
        if (!(Test-Path -LiteralPath $source) -or (Get-Item -LiteralPath $source).Length -eq 0) { throw "Missing release document: $name" }
        Copy-Item -LiteralPath $source -Destination $stage
    }
    $service = Join-Path $stage 'TemperatureService'
    New-Item -ItemType Directory -Path $service | Out-Null
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $build 'TemperatureService') -File) {
        if ($file.Extension -in @('.dll','.exe','.config') -or $file.Name -in @('Install.ps1','第三方许可.txt')) {
            Copy-Item -LiteralPath $file.FullName -Destination $service
        }
    }
    $notices = Join-Path $stage '运行时许可'
    New-Item -ItemType Directory -Path $notices | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $build '运行时许可') -File -Filter '*.TXT' | Copy-Item -Destination $notices
    $manifest = @()
    foreach ($file in Get-ChildItem -LiteralPath $stage -Recurse -File | Sort-Object FullName) {
        if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Package contains a link.' }
        $relative = $file.FullName.Substring($stage.Length+1).Replace('\','/')
        $manifest += [ordered]@{path=$relative;sha256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant();bytes=$file.Length}
    }
    $buildInfo = [ordered]@{
        version=$version;commit=$revision.Trim();sdk=(Get-Content -Raw (Join-Path $repo 'global.json')|ConvertFrom-Json).sdk.version
        runtime=[string]$project.Project.PropertyGroup.RuntimeFrameworkVersion
        architecture='win-x64';files=$manifest
    }
    $utf8 = [Text.UTF8Encoding]::new($false)
    [IO.File]::WriteAllText((Join-Path $stage 'build-info.json'), ($buildInfo|ConvertTo-Json -Depth 6), $utf8)
    # Sort entries and normalize timestamps. No local username, path or build time is recorded.
    Add-Type -AssemblyName System.IO.Compression
    $zip = Join-Path $output "FileDock-$version-win-x64.zip"
    $stream = [IO.File]::Open($zip,[IO.FileMode]::CreateNew)
    # .NET Framework recognizes the shared UTF8 instance when setting the ZIP UTF-8 flag.
    $archive = [IO.Compression.ZipArchive]::new($stream,[IO.Compression.ZipArchiveMode]::Create,$false,[Text.Encoding]::UTF8)
    try {
        foreach ($file in Get-ChildItem -LiteralPath $stage -Recurse -File | Sort-Object FullName) {
            $relative=$file.FullName.Substring($stage.Length+1).Replace('\','/')
            $entry=$archive.CreateEntry($relative,[IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime=[DateTimeOffset]::new(2000,1,1,0,0,0,[TimeSpan]::Zero)
            $source=[IO.File]::OpenRead($file.FullName);$destination=$entry.Open()
            try {$source.CopyTo($destination)} finally {$destination.Dispose();$source.Dispose()}
        }
    } finally { $archive.Dispose();$stream.Dispose() }
    $hash=(Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText((Join-Path $output 'SHA256SUMS.txt'), "$hash  $([IO.Path]::GetFileName($zip))`n", $utf8)
    & (Join-Path $PSScriptRoot 'Verify-Package.ps1') -Archive $zip -ExpectedCommit $revision.Trim()
    $currentRevision = & git rev-parse HEAD
    $changed = & git status --porcelain --untracked-files=normal
    if ($LASTEXITCODE -ne 0 -or $changed -or $currentRevision -ne $revision) { throw 'Source changed during packaging; discard this output and rebuild from a clean checkout.' }
    Write-Host "Release package: $zip"
} finally { Pop-Location }
