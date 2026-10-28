param([string]$OutputDirectory = (Join-Path $PSScriptRoot '..\重建输出'))
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path $PSScriptRoot -Parent
$output = [IO.Path]::GetFullPath($OutputDirectory)
# An empty destination prevents a previous build or personal Data folder entering a release.
if (Test-Path -LiteralPath $output) {
    if ((Get-Item -LiteralPath $output).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Output cannot be a directory link.' }
    if (@(Get-ChildItem -LiteralPath $output -Force).Count) { throw 'Output must be empty. Select a new output directory.' }
}
Push-Location $repo
try {
    $expectedSdk = (Get-Content -Raw -LiteralPath (Join-Path $repo 'global.json') | ConvertFrom-Json).sdk.version
    $sdk = & dotnet --version
    if ($LASTEXITCODE -ne 0 -or $sdk.Trim() -ne $expectedSdk) { throw "Install .NET SDK $expectedSdk (see global.json)." }
    $main = Join-Path $PSScriptRoot 'FileDockDemo.csproj'
    $service = Join-Path $PSScriptRoot 'TemperatureService\FileDockTemperature.csproj'
    $config = Join-Path $PSScriptRoot 'NuGet.Config'
    & dotnet restore $main -r win-x64 --locked-mode --configfile $config -p:SelfContained=true
    if ($LASTEXITCODE -ne 0) { throw 'Application locked restore failed.' }
    & dotnet restore $service --locked-mode --configfile $config
    if ($LASTEXITCODE -ne 0) { throw 'Temperature service locked restore failed.' }
    & dotnet publish $main -c Release -r win-x64 --self-contained true --no-restore -o $output -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) { throw 'Application build failed.' }
    $serviceOutput = Join-Path $output 'TemperatureService'
    & dotnet build $service -c Release --no-restore -o $serviceOutput -p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) { throw 'Temperature service build failed.' }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'TemperatureService\Install.ps1'),(Join-Path $PSScriptRoot 'TemperatureService\第三方许可.txt') -Destination $serviceOutput
    # Use notices shipped with the exact runtime packages restored above.
    $assets = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'obj\project.assets.json') | ConvertFrom-Json
    $cache = @($assets.packageFolders.PSObject.Properties.Name)[0]
    [xml]$project = Get-Content -LiteralPath $main
    $runtime = [string]$project.Project.PropertyGroup.RuntimeFrameworkVersion
    $notices = Join-Path $output '运行时许可'
    New-Item -ItemType Directory -Path $notices -Force | Out-Null
    foreach ($pack in @('microsoft.netcore.app.runtime.win-x64','microsoft.windowsdesktop.app.runtime.win-x64')) {
        $packRoot = Join-Path $cache "$pack\$runtime"
        $names = @('LICENSE')
        if ($pack -eq 'microsoft.netcore.app.runtime.win-x64') { $names += 'THIRD-PARTY-NOTICES' }
        foreach ($name in $names) {
            $file = Get-ChildItem -LiteralPath $packRoot -File | Where-Object { $_.BaseName -ieq $name } | Select-Object -First 1
            if (!$file -or $file.Length -lt 100) { throw "Missing runtime notice: $pack/$name" }
            Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $notices "$pack-$name.TXT")
        }
    }
    Write-Host "Built application and service: $output"
} finally { Pop-Location }
