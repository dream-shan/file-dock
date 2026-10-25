param(
    [string]$SourceDirectory = (Join-Path $PSScriptRoot '..\..\重建输出\温度服务'),
    [string]$ResultPath
)
$ErrorActionPreference = 'Stop'
try {
    $principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    if (!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw '请以管理员身份运行本安装脚本。主程序不需要管理员权限。' }
    $source = (Resolve-Path -LiteralPath $SourceDirectory).Path
    if (!(Test-Path -LiteralPath (Join-Path $source 'FileDockTemperature.exe'))) { throw '未找到已构建的温度服务。' }
    $target = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'FileDockTemperature'
    $exe = Join-Path $target 'FileDockTemperature.exe'
    $serviceName = 'FileDockTemperature'
    $existing = Get-CimInstance Win32_Service -Filter "Name='FileDockTemperature'"
    if ($existing -and $existing.PathName.Trim('"') -ne $exe) { throw '同名服务指向其他程序，已停止安装。' }
    if (Test-Path -LiteralPath $target) {
        if ((Get-Item -LiteralPath $target).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw '安装目录不能是目录链接。' }
        foreach ($entry in Get-ChildItem -LiteralPath $target -Recurse -Force) {
            if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw '安装目录包含链接，已停止安装。' }
        }
    } else { New-Item -ItemType Directory -Path $target | Out-Null }
    # Do not grant write access to ordinary users: this directory contains
    # executable code run by the Windows service.
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetAccessRuleProtection($true, $false)
    $acl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))
    foreach ($sid in @('S-1-5-18','S-1-5-32-544')) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid),'FullControl','ContainerInherit,ObjectInherit','None','Allow'))
    }
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new('S-1-5-32-545'),'ReadAndExecute','ContainerInherit,ObjectInherit','None','Allow'))
    Set-Acl -LiteralPath $target -AclObject $acl
    if ($existing) {
        $oldProcess = Get-Process -Id $existing.ProcessId -ErrorAction SilentlyContinue
        Stop-Service $serviceName -ErrorAction Stop
        if ($oldProcess -and !$oldProcess.WaitForExit(10000)) { throw '旧服务进程尚未退出，已停止覆盖。' }
    }
    foreach ($file in Get-ChildItem -LiteralPath $source -File) {
        if ($file.Extension -notin @('.exe','.dll','.config','.txt')) { continue }
        if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw '源文件不能是链接。' }
        $destination = Join-Path $target $file.Name
        Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
        if ((Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath $destination).Hash) { throw '安装文件校验失败。' }
    }
    if (!$existing) {
        New-Service -Name $serviceName -BinaryPathName ('"'+$exe+'"') -DisplayName '文件坞 CPU 温度' -StartupType Automatic -Description '仅按本机文件坞请求读取 CPU 温度；无请求时不采样。' | Out-Null
    } else { Set-Service $serviceName -StartupType Automatic }
    Start-Service $serviceName
    (Get-Service $serviceName).WaitForStatus('Running',[TimeSpan]::FromSeconds(15))
    if ($ResultPath) { @{Success=$true;Path=$target}|ConvertTo-Json|Set-Content -LiteralPath $ResultPath -Encoding UTF8 }
} catch {
    if ($ResultPath) { @{Success=$false;Error=$_.Exception.Message}|ConvertTo-Json|Set-Content -LiteralPath $ResultPath -Encoding UTF8 }
    throw
}
