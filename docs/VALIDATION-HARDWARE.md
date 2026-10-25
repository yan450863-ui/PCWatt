# 真实硬件与可选曲线验证

验证日期：2026-10-25。工程位于 `D:\工作间\PCWatt`，Windows 11 x64，.NET SDK 10.0.401，运行时 10.0.12。沿用当前用户普通权限和 Launch.cmd 启动方式，不改权限标签，不安装驱动，不申请管理员权限。

## 本轮交付

- 接入 LibreHardwareMonitorLib 0.9.6，只开启 CPU/GPU 类设备，仅读取 Power 类型传感器；负载百分比、TDP、功率上限不作为实际瓦数。
- CPU 读取前检查已经存在的 PawnIO 设备是否可访问；不可读时不启用该路径，避免底层空缓冲被误标为零功率。
- 每传感器有独立来源、硬件名、组件类型、范围、可用性、可信度和样本。组件不隐式求和，真实采集不创建模拟回退。
- 一个调度循环获取统一时间戳的多通道帧；每 30 秒或累计 6 帧在同一事务写入目录及样本。暂停、退出释放对象，失联通道补未知状态而非功率值，恢复后不会跨未知区间积分。
- “查看硬件”切换当前功率、今日耗电、预计电费、覆盖率与来源；下方勾选多条曲线，允许全关，设置可恢复。图形选择与后台记录范围独立。
- 后台模式精细 5 秒、细 10 秒、一般 30 秒、省电 1 分钟，默认一般；保留已有秒数配置，无新增并行定时器。
- SQLite schema 4 新增 power_channels，兼容 1/2/3 升级。旧文件名 simulation.db 保留，模拟历史单独标记，不迁移为真实样本。

## 自动化执行结果

实际执行，均成功：

```powershell
dotnet restore PCWatt.sln --locked-mode
dotnet build PCWatt.sln --no-restore
dotnet test PCWatt.sln --no-restore --logger "trx;LogFileName=hardware-debug.trx" --results-directory artifacts/test-results
dotnet build PCWatt.sln -c Release
dotnet test PCWatt.sln --no-build --no-restore -c Release --logger "trx;LogFileName=hardware-release.trx" --results-directory artifacts/test-results
dotnet publish src/PCWatt.App/PCWatt.App.csproj -c Release -r win-x64 --self-contained false -o artifacts/PCWatt-hardware-staging
```

Debug 与 Release 各 **152 通过，0 失败，0 跳过**。构建零警告、零错误。初次新增测试曾因遗漏 LoadAsync 的 fallbackPath 参数及使用过时 DataTestMethod 而编译失败，修复后完整重跑通过。

新增覆盖包括：有效/零/负数/空/NaN/无穷及异常大瓦数、单项失败不替换其他读数、硬件型号变化隔离历史键、单次初始化及资源释放、取消不打开硬件、初始化失败不生成模拟值、多通道共用一个循环、设备消失再出现的未知区间、暂停发现目录但不记样本、schema 3 迁移、目录与样本事务回滚、独立来源电量和计费恢复、选择切换期间的异步竞态、不可读来源不显示旧功率、图形全关仍记录及恢复、四档中文模式名，以及发现尚未完成时不错误默认选中旧模拟记录。

XAML 渲染测试在同一 WPF Application 中解析真实视图，新增 `artifacts/ui/hardware-dashboard.png`、`hardware-minimum.png`、`hardware-all-hidden.png`、`hardware-modes.png`。已查看默认布局及模式设置图片；固定测试样本仅用于渲染，不写用户数据库。最小窗口可滚动查看余下卡片。

## 本机真实硬件结果

通过系统 dotnet.exe 运行诊断探针，使用实际采集库读取两帧，并运行默认唯一采样器的精细档进行 17 秒独立数据库记录：

| 通道 | 实际结果 |
| --- | --- |
| NVIDIA GeForce RTX 5060 Ti / GPU 封装 | 可读；记录探针四次约 9.04–9.81 W |
| AMD Radeon(TM) Graphics / GPU 核心域 | 可读；四次约 0–29 W，真实零值保留 |
| AMD Radeon(TM) Graphics / SoC 共享域 | 可读；四次约 13–14 W |
| AMD Ryzen 7 9700X / CPU 功率 | 当前权限不可读；四次均为空，带缺少功率/传感器不可用状态 |

各来源分别 4 条，共 16 条；没有模拟标记或估算回退，LastError 为空，重新读取成功。读数随负载变化，只能证明此机此时接口可读，不能证明与墙插功率计一致。SoC 共享域可能包含多个组件的消耗，不与 GPU 封装或核心域相加。

诊断源码、有效 JSON 结果与隔离数据库位于 `artifacts/diagnostics/HardwareProbe`、`hardware-probe.json`、`hardware-recording.json` 及结果所指的 hardware-recording-*.db，未写入用户历史。

## 用户数据与实际启动

更新前确认 PC Watt 未运行，使用 SQLite backup API 备份原库和配置至 `artifacts/recovery/hardware-20261025-0200`。原库 schema 3，28 条功率记录、1 个电价版本；通知开关启用、记录开启、采样 30 秒等当前设置保持。

将已通过验证的发布目录切换到 `artifacts/PCWatt-preview`，原预览保留为 `artifacts/PCWatt-preview-before-hardware-20261025`。通过根目录启动入口实际打开，主窗口标题为“PC Watt · 硬件能耗”，进程响应正常，主进程由系统 dotnet.exe 承载。

等到正常批量提交后，验证脚本确认：schema 4、integrity_check=ok；所有旧表原有行逐字段保留、原有设置逐字段保持；首轮已新增 8 条真实硬件样本、保存 4 个通道，所有新增样本来源为 lhm: 且没有模拟/估算标记。检查最初在批量提交前未读到目录，等待既定 30 秒提交后验证通过，没有为验收改变后台频率或强制逐样本写库。验证快照保存在备份目录的 before.json、after.json。

## 发布与许可

框架依赖 Windows x64 发布包位于 `artifacts/PCWatt-hardware-win-x64-framework-dependent.zip`，共 41 个文件，4,553,113 字节，CRC 校验通过。根目录启动入口及独立包 Launch.cmd 保持；再次启动后仍只有一个 PC Watt 主进程，窗口响应正常。发布清单已核对必要启动文件、硬件 DLL、许可及模块源码，不含用户设置、用户数据库或测试程序集；默认 appsettings.json 随包提供。检查结果保存在 `artifacts/diagnostics/hardware-package.json`。

硬件库采用 MPL-2.0，保留上游第三方声明及内嵌 PawnIO.Modules 的 LGPL 文本和 0.1.6 对应完整源码档案，传递依赖许可已登记；自编代码继续采用 MIT。详见根目录 THIRD-PARTY-NOTICES.md。

## 尚未验收的范围

本轮没有验证 Windows 10、其他 CPU/GPU、登录自启动、休眠/关机、所有托盘操作、系统是否展示通知、DPI 全档或动画/驱动调用资源占用。硬件调用受厂商库限制，取消在读取前后检查，不保证正在执行的同步厂商调用立刻中断。传感器可信度为中，未做外部功率计校准。

完整小时/天/周/月/年统计、报告导出、备份恢复及清除界面仍按后续阶段开发；本次图形是最近十分钟功率（W），所选来源的今日电量和费用显示在卡片上。
