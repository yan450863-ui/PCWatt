# 日月年图表联动验证

日期：2026-10-25。Windows 11 x64，.NET SDK 10.0.401。

## 确认行为

- 保留暖白纸与墨青主题、自绘标题栏、卡片布局、实时功率、温度和占用率。
- 电费卡“今日 / 本月 / 今年”同步切换下方耗电柱状图：今日按小时、本月按天、今年按月；单位度/kWh。标题、横轴、累计电量、覆盖率与悬停区间同步，不再固定显示最近十分钟。
- 当前功率卡继续显示实时 W 和最近十分钟平均/峰值；周期图展示所选部件耗电，电费卡始终整机计费。整机图包含历史整机方案的非重叠有效区间。
- 空白未知与有效零值分开，部分覆盖使用琥珀色柱顶；悬停说明时间范围、电量、覆盖率、估算/模拟属性及缺失。只创建已到达时间的分组，当前组裁切至统计截止时刻，不画未来零值。
- 跨小时、午夜和月边界按原始线性功率段裁切，保留两侧相邻样本，重复不累计，过长/无效/暂停/时钟异常不补算。整机重叠方案仍记为未知；历史日报允许使用当前已经采到的右侧邻点，修正以往日末少算末段的问题，不读取系统当前时间之后的样本。
- 图表读取和年度整理在工作线程执行，没有新增采样或 UI 轮询循环。快速切换以当前请求版本为准，旧结果不覆盖新周期；年度整机图与账单使用相同截止时刻，沿用年度账单 20 秒缓存，并缓存相同查询结果。

## 自动化与渲染

Debug、Release 的 `dotnet build PCWatt.sln --no-restore` 和 `dotnet test PCWatt.sln --no-build --no-restore`（Release 加 `-c Release`）均成功：各 **230 通过、0 失败、0 跳过**，构建零警告、零错误。锁定依赖还原和 win-x64 framework-dependent 发布通过。

新增 10 项测试，覆盖分组积分守恒、跨小时/午夜/月、重复点、异常/暂停/时钟边界、未知与真实零值、未来样本排除、整机多方案/重叠/模拟、闰月与 23/25 小时夏令时日、SQLite 恢复和来源隔离、缓存、日月年/设备联动，以及快速切换防止旧结果覆盖。结果：`artifacts/test-results/period-chart-debug.trx`、`period-chart-release.trx`。

实际 XAML 六张渲染图在 `artifacts/ui/`：

- `period-chart-day-fixture.png`、`period-chart-day-minimum-fixture.png`
- `period-chart-month-fixture.png`、`period-chart-month-minimum-fixture.png`
- `period-chart-year-fixture.png`、`period-chart-year-minimum-fixture.png`

图中均为固定测试数据，带水印，不写用户数据库。测试校验分段高亮、实际图表绑定的周期/分组数/范围文字，以及默认和最小窗口内完整可见。原生 Windows 窗口的鼠标悬停和全部 DPI 尚未单独实机验收。

## 实际历史核对与发布

对更新前数据库副本运行独立探针：今日 12 个小时组、本月 25 个日组、今年 10 个月组，整机有效耗电约 0.222942451 度。三种周期分组之和均与相同截止时刻的账单完全一致，覆盖时长一致；目前记录集中于当天，图中只有实际有记录的分组有电量，其他组未知。此值只是副本内有效记录，不能视为完整全天或全年耗电。结果：`artifacts/diagnostics/period-chart-data.json`。

当前运行目录 `artifacts/PCWatt-period-chart-preview`；发布包 `artifacts/PCWatt-period-chart-win-x64-framework-dependent.zip` 共 47 文件，启动器、许可、LGPL 模块源码与 CRC 检查通过，无用户数据库、设置、日志或测试工具。大小与 SHA256：`artifacts/diagnostics/period-chart-package.json`。

更新前数据库、设置、根入口、自启动目标和注册项命令备份于 `artifacts/recovery/period-chart-20261025`。9298 条样本、2 个电价版本、1 条通知日期均保留，schema 5 无迁移、完整性正常，用户设置逐字段不变（包括 10 秒模式、整机视图）。根入口和用户启动器目标更新，HKCU Run 命令不变，启动器文件与 ZIP 主 DLL 核对一致；结果为备份目录 `verified.json`。

旧管理员实例未被强制退出，其目录未被覆盖。托盘正常退出后重新打开根入口加载新版。没有新增小时/周范围、任意历史日期选择、导出或数据清理功能。
