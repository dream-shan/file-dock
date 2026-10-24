param([string]$OutputDirectory = (Join-Path $PSScriptRoot '..\重建输出'))
$ErrorActionPreference = 'Stop'
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw '请先安装 .NET 10 SDK。运行演示版不需要安装 SDK。' }
& dotnet publish (Join-Path $PSScriptRoot 'FileDockDemo.csproj') -c Release -r win-x64 --self-contained true -o $OutputDirectory -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true --configfile (Join-Path $PSScriptRoot 'NuGet.Config')
if ($LASTEXITCODE -ne 0) { throw '构建失败，请查看上方错误。' }
Write-Host "已生成：$OutputDirectory\FileDockDemo.exe"
