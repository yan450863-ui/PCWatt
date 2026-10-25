# 整机、硬件估算与启动验证

验证日期：2026-10-25，Windows 11 x64，.NET SDK 10.0.401。项目路径 `D:\工作间\PCWatt`。

## 当前行为

- 整机 / 硬件两类视图，所选来源的功率、积分、电费与覆盖率保持一致。
- CPU 仅使用封装总功率，Core、SoC、PPT 不重复加入；已知核显不与 CPU 重叠累计。
- 默认自动识别独显和其他部件，无需填写 CPU 来源、电源效率等参数。高级手动校准默认收起，可恢复自动方案。
- 内存、磁盘、风扇、主板其余部分优先采用有效瓦数，缺少时使用明确标记的通用估算。转速、温度、负载与容量不作为瓦数。
- 通用常数：内存总计 8 W、每个已识别物理磁盘 3 W、每路已识别风扇 2 W、主板其余部分 12 W。无法识别磁盘或风扇数量时分别采用整类 3 W / 4 W。电源效率按 90% 估算。这些值没有经过本机校准。
- 整机为上述来源合计除以估计效率，始终是低可信度估算，不含显示器。CPU 或必要 GPU 数据缺失、过期或范围不安全时保持未知，不补零。
- 实测和估算来源使用不同 ID；自动方案根据设备和来源生成稳定 ID。规则/来源变化不会重算旧样本；当前整机视图只显示当前方案，旧历史保留。
- CPU 下拉框显示硬件名称；主界面硬件项增量更新，自动刷新不清空选择框。动效、通知、手动校准按需展开。

## 权限与自启动

默认开启“启动时读取完整硬件”。普通启动在唯一实例确认、读取设置后，通过已部署的无窗口启动器请求正常 Windows UAC；启动器创建高权限 .NET 主进程后退出，不保留额外常驻进程。高权限实例等待原实例正常保存退出，再接手唯一采样循环。

取消授权或 Windows 拒绝启动时继续普通权限记录可用硬件与部件估算；设置中可独立关闭该默认请求，下次启动生效。没有创建高权限计划任务、修改 ACL/完整性标签或安装驱动。管理员身份也不保证所有设备都有功率接口。

当前用户自启动注册项直接调用 `%LOCALAPPDATA%\PCWatt\Launcher\PCWatt.Launcher.exe --background`，主软件仍在 D 盘；该入口无窗口并支持中文、空格路径。启动日志在用户数据目录 `logs/startup.log` 与 `launcher.log`，会轮换一份。

此前原始自启动报错没有保存可定位的日志，不断言其历史根因。当前启动器注册和命令重放已验证；真实 Windows 注销/登录尚未执行，不声称登录场景已完成验收。

## 实机结果

普通权限探针已读取 RTX 5060 Ti 及 AMD 核显功率域，CPU 权限不可用时保持未知。Windows 已识别 ZHITAI Ti600 1TB，未取得其瓦数，显示单独的硬盘估算。

正常启动入口的 App 日志记录普通进程 `integrity=0x2000`，授权交接后主进程 `integrity=0x3000`，随后 `ready; sampling started; tray=True`。实际界面已看到 Ryzen 7 9700X 的 CPU 功率；后台数据库有真实 CPU/GPU 样本和有效整机估算。两路 Nuvoton 风扇只提供转速，内存、SSD、风扇与主板其余部分在当前机器上均明确标记为估算，未冒充实测。

固定数据布局图不是实机测量。布局测试图 `whole-*.png`、`settings-*-fixture.png` 带测试水印；测试不写入用户数据库。实际窗口读取使用 computer-use 技能，没有点击 Windows 授权窗口或更改系统安全设置。

## 自动化结果

实际执行：

```powershell
dotnet restore PCWatt.sln --locked-mode
dotnet build PCWatt.sln --no-restore
dotnet test PCWatt.sln --no-build --no-restore
dotnet build PCWatt.sln -c Release --no-restore
dotnet test PCWatt.sln -c Release --no-build --no-restore
dotnet publish src/PCWatt.App/PCWatt.App.csproj -c Release -r win-x64 --self-contained false --no-restore -o artifacts/PCWatt-easy-final-staging
dotnet restore PCWatt.sln --locked-mode
```

Debug / Release 各 **185 通过，0 失败，0 跳过**；构建零警告、零错误。结果：`artifacts/test-results/easy-debug.trx`、`easy-release.trx`。发布后锁定还原仍通过。

在原有积分、跨午夜、重复样本、异常功率/间隔、电价、恢复、调度、通知、迁移与自启动测试基础上，覆盖：

- 实测优先，真实零值有效；空/负值改用单独的估算来源。
- 同型号物理磁盘不合并；转速信息不成为功率。
- 自动整机公式、核显/独显域去重、缺失 CPU/GPU 保持未知。
- 来源实测/估算转换产生新规则 ID；估算元数据和积分可从 SQLite 恢复。
- 默认权限/自动估算设置、恢复自动方案保留采样频率。
- Windows 授权参数及取消/失败回退，测试使用替身、不弹 UAC；日志也注入本地测试集合。
- 实际 XAML 的 CPU 友好名称、刷新后硬件选择保持、默认/最小尺寸和简化设置布局。

## 数据与发布

更新前完整备份至 `artifacts/recovery/easy-hardware-20261025`：schema 5、292 条样本、1 个电价版本、1 条通知日期。保留用户已选 10 秒频率、视图、曲线掩码等设置，新增管理员和自动估算两个独立默认字段。

数据核对脚本确认 `integrity_check=ok`，全部旧样本、电价、通知日期、旧通道 ID 和已有设置保留。新组件估算带估算标记，CPU/GPU 样本没有模拟或估算标记，整机估算有低可信度与估算标记；没有写入新的模拟数据。结果在备份目录 `after.json`。

发布包 `artifacts/PCWatt-easy-hardware-win-x64-framework-dependent.zip` 共 47 个文件，包含无窗口启动器、完整许可和 LGPL 模块源码，不含用户数据库、设置、日志、个人启动目标或测试工具。ZIP CRC 与必需文件检查通过，SHA256 与体积保存在 `artifacts/diagnostics/easy-package.json`。

当前入口和发布以 [简洁界面验证](VALIDATION-SIMPLE-UI.md) 为准，旧硬件选择框已由分类按钮替代。

## 尚未验证

真实 Windows 注销/登录、真实取消授权后的窗口流程、Windows 10、其他硬件组合、完整 DPI、睡眠/关机与资源占用仍待实机验收。没有外部电表对照校准，估算不能视为精确的整机用电或电力公司账单。
