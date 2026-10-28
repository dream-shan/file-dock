# 第三方组件与分发说明

文件坞自身代码的许可见根目录 `LICENSE`。该许可不替代下列组件的许可，也不限制接收者按其原许可取得、使用和修改这些组件源码的权利。当前未修改这些第三方组件。

## 温度服务

直接和传递依赖的版本、内容哈希见 `源码/TemperatureService/packages.lock.json`。完整的 MPL-2.0、Apache-2.0、MIT 许可及现有版权声明见 `源码/TemperatureService/第三方许可.txt`；下载包中的对应文件为 `TemperatureService/第三方许可.txt`。

| 组件 | 版本 | 许可 | 对应源码或项目 |
| --- | --- | --- | --- |
| LibreHardwareMonitorLib | 0.9.6 | MPL-2.0 | [对应源码](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/tree/3d331e3370efb858411f19511373eff65a218701) |
| BlackSharp.Core | 1.0.7 | MPL-2.0 | [对应源码](https://github.com/Blacktempel/BlackSharp/tree/c70b735c6cec123ee8a046ac4a0bc6c606f52cf0) |
| DiskInfoToolkit | 1.1.2 | MPL-2.0 | [对应源码](https://github.com/Blacktempel/DiskInfoToolkit/tree/25319eae5781e75bcf141e844ceab2afe94d40ea) |
| RAMSPDToolkit-NDD | 1.4.2 | MPL-2.0 | [对应源码](https://github.com/Blacktempel/RAMSPDToolkit/tree/3b47b960e0830fef344624ad5e389675d5f0a1ce) |
| HidSharp | 2.6.4 | Apache-2.0 | [项目](https://software.seekye.com/hidsharp) |
| System.Buffers | 4.6.1 | MIT | [源码](https://github.com/dotnet/maintenance-packages) |
| System.CodeDom / System.Management | 10.0.2 | MIT | [源码](https://github.com/dotnet/dotnet) |
| System.Memory | 4.6.3 | MIT | [源码](https://github.com/dotnet/maintenance-packages) |
| System.Numerics.Vectors | 4.6.1 | MIT | [源码](https://github.com/dotnet/maintenance-packages) |
| System.Runtime.CompilerServices.Unsafe | 6.1.2 | MIT | [源码](https://github.com/dotnet/maintenance-packages) |
| System.Security.AccessControl | 6.0.0 | MIT | [源码](https://github.com/dotnet/runtime) |
| System.Security.Principal.Windows | 5.0.0 | MIT | [源码](https://github.com/dotnet/runtime) |
| System.Threading.AccessControl | 10.0.3 | MIT | [源码](https://github.com/dotnet/dotnet) |

`.NET Framework ReferenceAssemblies` 1.0.3 是编译期引用包，不作为运行库打入下载包。System.Management 在 net48 使用系统框架提供的程序集；还原锁文件仍记录其 NuGet 依赖。

PawnIO 是由用户从 [官方网站](https://pawnio.eu/) 单独取得并安装的签名驱动，本仓库及下载包不包含该驱动。主程序不需管理员运行；安装温度服务和驱动需要管理员确认。

## 主程序运行时

自包含 Windows x64 下载包使用 .NET / Windows Desktop Runtime 10.0.14，其版本在项目文件中锁定。构建脚本从这两个**实际还原的运行时包**复制其 LICENSE，以及 .NET Runtime 随包提供的 THIRD-PARTY-NOTICES，放入下载包的 `运行时许可/`。这些声明随二进制一起分发，不能仅保留文件坞自己的许可证。

构建使用 `global.json` 指定的 .NET SDK；SDK 本身不随文件坞分发。仓库中历史的 `运行时许可/` 文本予以保留，新包使用实际构建依赖带来的声明。

## 后续维护

升级第三方依赖时须同时更新锁文件、上述清单及完整许可文本。若修改 MPL 组件源码，须向接收者提供对应修改后的源码，并保留其 MPL 声明；不能只链接未修改的上游版本。重新分发本下载包时，请保留 `LICENSE`、本文件及组件许可文件。
