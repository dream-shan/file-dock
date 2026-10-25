param([string]$OutputDirectory = (Join-Path $PSScriptRoot '..\重建输出'))
$ErrorActionPreference = 'Stop'
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw '请先安装 .NET 10 SDK。运行演示版不需要安装 SDK。' }
& dotnet publish (Join-Path $PSScriptRoot 'FileDockDemo.csproj') -c Release -r win-x64 --self-contained true -o $OutputDirectory -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:UseSharedCompilation=false ("-p:RestoreConfigFile=" + (Join-Path $PSScriptRoot 'NuGet.Config'))
if ($LASTEXITCODE -ne 0) { throw '构建失败，请查看上方错误。' }
Write-Host "已生成：$OutputDirectory\FileDockDemo.exe"
$serviceOutput = Join-Path $OutputDirectory '温度服务'
& dotnet build (Join-Path $PSScriptRoot 'TemperatureService\FileDockTemperature.csproj') -c Release -o $serviceOutput -p:UseSharedCompilation=false ("-p:RestoreConfigFile=" + (Join-Path $PSScriptRoot 'NuGet.Config'))
if ($LASTEXITCODE -ne 0) { throw '温度服务构建失败。' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'TemperatureService\第三方许可.txt') -Destination $serviceOutput -Force
Write-Host '温度服务已构建。首次安装请以管理员身份运行 源码\TemperatureService\Install.ps1；主程序正常运行即可。'