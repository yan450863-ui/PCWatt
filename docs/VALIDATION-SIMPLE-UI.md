# 简洁界面验证

日期：2026-10-25。Windows 11 x64，.NET SDK 10.0.401。

## 界面与行为

- 顶部固定五项：整机、CPU、显卡、内存、硬盘。风扇和主板没有首页入口，仍由原采样链记录并参与整机估算。
- 点击分类后，当前功率、今日用电、电费、覆盖率和曲线选择同一来源。默认选组件总功率，显卡 Core/SoC 等重叠域不列为独立入口。
- 同类只有一个设备时不出现第二组选项；有多个设备时显示“独立显卡 / 集成显卡”或“硬盘 1 / 硬盘 2”。集显只有局部功率时保留“局部读数”说明。
- 首页每次仅显示一条曲线，删除长传感器勾选列表与模拟历史入口。旧图形掩码仍保存在设置中，不影响新的单来源视图，也不删除历史数据。
- 三张数值卡片、估算/局部读数小标记、更新时刻和覆盖率直接可见。来源、可信度、缺失信息、计算规则在默认关闭的“记录详情”中。
- 默认窗口 1120×800；低功率曲线自动采用较小的纵轴范围，避免内存/硬盘曲线贴底。原有动效、缺失断线和后台采样逻辑保留。

## 实际验证

执行 `dotnet build PCWatt.sln --no-restore`、`dotnet test PCWatt.sln --no-build --no-restore`，并对 Release 执行相同构建测试。两种配置各 **191 通过，0 失败，0 跳过**，构建零警告、零错误。TRX 在 `artifacts/test-results/simple-debug.trx`、`simple-release.trx`。

新增交互测试覆盖五项分类、隐藏小部件/模拟/重叠域、统计和曲线同步、设备选择保持、无读数/权限提示、估算标记，以及旧设置掩码兼容。选择和刷新没有调用硬件采集方法。

实际 XAML 已渲染默认和最小尺寸，校验五个按钮、默认收起详情、没有传感器复选框、默认窗口内完整显示曲线。预览 `artifacts/ui/simple-whole-fixture.png`、`simple-storage-fixture.png`、`simple-memory-fixture.png` 带“测试数据，非真实测量”水印；测试不访问真实用户数据库。

Release 已发布至 `artifacts/PCWatt-simple-preview`，随后 `dotnet restore PCWatt.sln --locked-mode` 通过。发布包 `artifacts/PCWatt-simple-ui-win-x64-framework-dependent.zip` 的 47 个文件、必需启动器/许可/模块源码和 CRC 均通过检查，无用户数据库、设置、日志、个人启动目标或测试工具。体积与 SHA256 在 `artifacts/diagnostics/simple-package.json`。

## 部署与保留

更新前备份数据库、设置、启动入口和用户启动目标至 `artifacts/recovery/simple-ui-20261025`，其中有 1562 条旧样本、1 个电价版本和 1 条通知日期。

根目录“启动.cmd”与用户启动器的 target.json 已指向独立的简洁版目录，HKCU Run 的启动器命令不变。没有改动正在运行的旧目录、强制结束进程或清除数据；用户从托盘正常退出后，重新打开入口即可使用新界面。所有已有频率、电价、通知和显示设置保留。

部署核对已确认 SQLite 完整性正常、全部旧样本/电价/通知日期保留、设置逐字段一致、入口与自启动目标正确、已安装启动器四个文件与发布文件一致、ZIP 主 DLL 与运行目录一致。结果保存在备份目录 `verified.json`。

新版的实际管理员窗口点击流程尚未单独验收；现有管理员实例仍可持续记录。原硬件/权限实机结果见 [整机与启动验证](VALIDATION-WHOLE-PC.md)，Windows 登录、Windows 10、完整 DPI 和资源占用边界仍适用。
