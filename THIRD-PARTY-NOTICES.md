# Third-party notices

PC Watt 自编代码的 MIT 许可不替代第三方许可。本文件用于登记实际依赖，并作为以后添加依赖的声明模板。完整解析版本见各项目 `packages.lock.json`。

## 直接依赖

| 组件 | 版本 | 用途 | 上游许可与来源 |
| --- | --- | --- | --- |
| Microsoft.Data.Sqlite | 10.0.13 | SQLite ADO.NET provider | MIT · https://github.com/dotnet/efcore |
| LibreHardwareMonitorLib | 0.9.6 | CPU/GPU/DRAM/磁盘功率及主板风扇识别 | MPL-2.0 · https://github.com/LibreHardwareMonitor/LibreHardwareMonitor |
| Microsoft.NET.Test.Sdk | 18.11.0 | 仅测试 | MIT · https://github.com/microsoft/vstest |
| MSTest.TestAdapter | 4.5.1 | 仅测试 | MIT · https://github.com/microsoft/testfx |
| MSTest.TestFramework | 4.5.1 | 仅测试 | MIT · https://github.com/microsoft/testfx |

## 已核对的运行时传递依赖

核对日期：2026-10-25。依据实际还原的 App / Infrastructure lock file、NuGet nuspec 及上游许可；不将本机无关缓存包纳入清单。

| 组件 | 版本 | 许可 | 版权 / 来源 |
| --- | --- | --- | --- |
| Microsoft.Data.Sqlite.Core | 10.0.13 | MIT | Microsoft Corporation；.NET Foundation and Contributors · https://github.com/dotnet/efcore |
| SQLitePCLRaw.bundle_e_sqlite3 | 2.1.13 | Apache-2.0 | Copyright 2014–2024 SourceGear, LLC · https://github.com/ericsink/SQLitePCL.raw |
| SQLitePCLRaw.core | 2.1.13 | Apache-2.0 | 同上 |
| SQLitePCLRaw.lib.e_sqlite3 | 2.1.13 | Apache-2.0 | 同上；含平台原生 SQLite 二进制 |
| SQLitePCLRaw.provider.e_sqlite3 | 2.1.13 | Apache-2.0 | 同上 |
| BlackSharp.Core | 1.0.7 | MPL-2.0 | Florian K. · https://github.com/Blacktempel/BlackSharp/tree/c70b735c6cec123ee8a046ac4a0bc6c606f52cf0 |
| DiskInfoToolkit | 1.1.2 | MPL-2.0 | Florian K. · https://github.com/Blacktempel/DiskInfoToolkit/tree/25319eae5781e75bcf141e844ceab2afe94d40ea |
| RAMSPDToolkit-NDD | 1.4.2 | MPL-2.0 | Florian K. · https://github.com/Blacktempel/RAMSPDToolkit/tree/3b47b960e0830fef344624ad5e389675d5f0a1ce |
| HidSharp | 2.6.4 | Apache-2.0 | Copyright 2010–2025 James F. Bellinger · https://github.com/IntergatedCircuits/HidSharp |
| Mono.Posix.NETStandard | 1.0.0 | 上游 Mono 运行库 MIT 及所附通知 | Microsoft Corporation / Mono contributors · https://github.com/mono/mono |
| System.CodeDom / System.Management | 10.0.2 | MIT | .NET Foundation and Contributors · https://github.com/dotnet/runtime |
| System.IO.Ports / runtime.native.System.IO.Ports 及平台 runtime.* 包 | 10.0.3 | MIT | 同上；Windows 发布仅选择适用的资产 |
| System.IO.FileSystem.AccessControl | 5.0.0 | MIT | 同上 |

完整许可文本：[Microsoft.Data.Sqlite MIT](licenses/Microsoft.Data.Sqlite-LICENSE.txt)、[Apache 2.0](licenses/Apache-2.0.txt)。上游库未修改。SQLite 原生代码自身的公共领域声明见 https://www.sqlite.org/copyright.html；打包层的 Apache-2.0 条款仍保留。

## 已核对的仅测试传递依赖

| 组件 | 版本 | nuspec 许可 |
| --- | --- | --- |
| Microsoft.ApplicationInsights | 2.23.0 | MIT |
| Microsoft.CodeCoverage | 18.11.0 | MIT |
| Microsoft.Testing.Extensions.Telemetry | 2.5.1 | MIT |
| Microsoft.Testing.Extensions.TrxReport.Abstractions | 2.5.1 | MIT |
| Microsoft.Testing.Platform | 2.5.1 | MIT |
| Microsoft.Testing.Platform.MSBuild | 2.5.1 | MIT |
| Microsoft.TestPlatform.ObjectModel | 18.11.0 | MIT |
| Microsoft.TestPlatform.TestHost | 18.11.0 | MIT |
| MSTest.Analyzers | 4.5.1 | MIT |

这些包只在开发/测试时使用，不属于 App 的运行时发布依赖。本项目不将测试工具重打包分发。若将来分发这些工具，必须携带它们各自的完整版权/许可/NOTICE，不能只携带本项目 MIT 许可。

## 硬件库、内嵌模块与源码取得方式

LibreHardwareMonitorLib 0.9.6 保持上游二进制不变，源码对应 NuGet 记录的提交 [3d331e3370efb858411f19511373eff65a218701](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/tree/3d331e3370efb858411f19511373eff65a218701)。其余三个 MPL 依赖也未修改，准确源码链接见上表。MPL 组件的源码继续按 MPL-2.0 提供，本项目自编适配器与 UI 使用 MIT，不改变上游许可。

完整文本随程序提供：[MPL-2.0](licenses/MPL-2.0.txt)、[LibreHardwareMonitor 上游第三方声明](licenses/LibreHardwareMonitor-THIRD-PARTY-NOTICES.txt)、[HidSharp 原始版权及许可](licenses/HidSharp-LICENSE.txt)、[Mono 上游许可与通知](licenses/Mono.Posix-LICENSE.txt)、[.NET MIT](licenses/dotnet-runtime-LICENSE.txt) 和 [.NET 第三方声明](licenses/dotnet-runtime-THIRD-PARTY-NOTICES.txt)。上游声明中的 Aga.Controls 用于其桌面应用，本项目没有使用该控件，仍完整保留原始通知。

LibreHardwareMonitor DLL 包含 PawnIO.Modules 0.1.6 模块资源，其许可为 LGPL-2.1-or-later（Copyright namazso and contributors）。保留 [原始源码指引](licenses/PawnIO-Modules-SOURCE.txt)、[COPYING](licenses/PawnIO-Modules-COPYING.txt)，并在发布目录附带未修改的 [完整模块源码档案](licenses/sources/PawnIO.Modules-0.1.6.zip)，来源为 [官方 0.1.6 发布](https://github.com/namazso/PawnIO.Modules/releases/tag/0.1.6)。模块按原库方式使用，硬件库独立 DLL 可替换；本项目不限制为调试该库修改而进行的逆向工程。需要重新构建库时，取得上述对应版本源码，按上游构建说明生成并替换 DLL，保留兼容的接口和依赖。

PC Watt 不分发或安装 PawnIO 内核驱动/安装器。根据用户确认的产品设置，启动默认通过 Windows UAC 请求管理员授权，取消后继续普通权限采集；可关闭该设置。只有已存在且当前进程可访问的接口才启用 CPU 和主板读取。启用存储识别，内存功率优先读取 CPU 暴露的 DRAM 域；不启用内存 SPD 组、额外 USB 控制器或 PSU 组，不调用风扇控制功能。风扇转速只用于识别已连接风扇，不能当作瓦数；没有瓦数接口时使用自编估算规则，明确标记为估算。

App 项目配置将本项目许可、本声明和 `licenses/` 复制到构建/发布目录；新增依赖或发行版前按实际依赖更新本文件。0.1.0 安装包和解压包自包含 Microsoft.NETCore.App 与 Microsoft.WindowsDesktop.App 10.0.12 win-x64，运行时二进制未修改；附带对应 NuGet 包中的原始 .NET MIT、.NET 第三方声明及 Windows Desktop LICENSE（licenses/dotnet-windowsdesktop-LICENSE.txt）。框架依赖开发预览仍使用已安装的 .NET/WPF。

安装器使用官方 Inno Setup 7.1.0 生成，保留其原始版权与许可提示，完整许可随程序提供于 licenses/Inno-Setup-LICENSE.txt。构建工具本体不包含在发布包中；上游为 https://jrsoftware.org/ 。安装器许可不替代 PC Watt 或其他组件的许可。

## 参考元数据

`CurrencyCatalog` 内部兼容读取旧币种元数据，币种代码及小数位参考 SIX 发布于 2026-09-17 的 [ISO 4217 List One](https://www.six-group.com/dam/download/financial-information/data-center/iso-currrency/lists/list-one.xml)。仅保存代码与数值事实，没有引入第三方执行代码或复制完整数据表；当前设置和计费显示仅提供人民币，运行时不连接该网站。

## 新增组件模板

```text
Component:
Exact version / commit:
Purpose and scope (runtime / development / test):
Upstream URL:
License identifier and full-text location:
Copyright / NOTICE requirements:
Modifications (if any):
Distributed files / platform assets:
Verification date:
```
