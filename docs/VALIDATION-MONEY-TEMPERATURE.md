# 电费与温度验证

日期：2026-10-25。Windows 11 x64，.NET SDK 10.0.401。

## 界面和计费

- 首页最大卡片为今日整机电费，其次为本月整机电费，显示已记录电量与覆盖率。整机、CPU、显卡、内存、硬盘五项只切换当前功率和一条曲线，不改变费用。
- 今日范围为本地零点至今，本月为本地月初至今，不预测整日或整月。按历史生效电价计算，不使用舍入后的显示金额重算，不计整户固定月费。
- 分别积分所有整机方案，拼接非重叠有效段；方案切换、暂停、超时、异常功率和时钟变化不补算。两套整机有效段重叠时记为 SourceConflict 未知，不重复收费；组件和模拟记录被排除。
- 没有有效整机记录、没有适用电价或部分时段无法用人民币计价时显示未知并解释。有效零功率/零电价可显示 0.00，正费用不足一分显示“< 0.01 元”。昨日报告与设置报告统一采用整机口径。

## 温度

- 同一次硬件 Update 同时读功率和 CPU/显卡温度，使用原硬件会话和唯一采样循环。温度独立建模，不进入功率表、积分或收费，无 SQLite 迁移。
- CPU 优先 Tdie、Tctl/Tdie、Package，保留 Tctl 等可用来源名称；显卡使用 GPU Core/GPU，不将 Hot Spot 或显存温度冒充核心温度。
- 两个简单温度读数位于曲线上方，切换显卡时跟随设备；其他类别优先独显。悬停说明来源和更新时刻，暂停、过期、未来时间、无权限或无效数据均不显示数值，不推算温度。
- 普通权限硬件探针两帧实际读到 RTX 5060 Ti 核心 31.77°C / 31.24°C；核显未提供所选核心读数，CPU 在普通权限下不可读。本轮没有验收管理员 CPU 温度或所有型号支持情况，不沿用历史 CPU 功率验证冒充温度验收。结果见 `artifacts/diagnostics/money-temperature-hardware.json`。

## 自动化与渲染

实际执行 Debug、Release 的 `dotnet build PCWatt.sln --no-restore` 与 `dotnet test PCWatt.sln --no-build --no-restore`（Release 加 `-c Release`），两种配置各 **207 通过、0 失败、0 跳过**，构建零警告、零错误。锁定依赖还原与 win-x64 framework-dependent 发布均成功。

新增 16 项测试覆盖：整机多方案/重叠/重复/异常、跨午夜电价、跨月恢复与来源隔离、切换组件保持费用、微小/零/无效计价、待写与保存恢复一致、午夜无新采样仍刷新费用；温度来源优选、异常值、硬件同读、单循环与暂停释放、显卡跟随和过期/未来/暂停状态。

TRX：`artifacts/test-results/money-temperature-debug.trx`、`money-temperature-release.trx`。实际 XAML 在默认和最小尺寸渲染，检查今日费用、温度绑定及完整曲线可见；预览 `artifacts/ui/money-temperature-fixture.png` 与 `money-temperature-minimum-fixture.png` 带测试水印，不代表本机读数，不写用户数据库。

合成 31 天、30 秒间隔的 89281 条整机样本单次计费约 156 ms，得到 74.4 kWh、59.52 元、100% 覆盖。此结果只衡量该计算器，不代表完整软件资源占用，见 `artifacts/diagnostics/money-temperature-benchmark.json`。

## 发布与数据保留

新目录 `artifacts/PCWatt-money-temperature-preview`，发布包 `artifacts/PCWatt-money-temperature-win-x64-framework-dependent.zip`。包内 47 个文件，必需启动器、许可与 LGPL 模块源码齐全，CRC 通过，没有用户数据、设置、日志、个人启动目标或测试工具；大小和 SHA256 见 `artifacts/diagnostics/money-temperature-package.json`。

更新前数据库、设置、根启动入口、自启动目标和注册项命令备份于 `artifacts/recovery/money-temperature-20261025`。3402 条已有样本、1 个电价版本、1 条通知日期保留；SQLite schema 5 和完整性正常，用户设置逐字段一致（包括现有 10 秒模式、整机视图和 CPU 选择）。

根启动入口与用户启动器 target.json 指向新目录，HKCU Run 命令未改；启动器四个文件与发布一致，ZIP 的 App/Core/Infrastructure DLL 与运行目录一致。部署检查见备份目录 `verified.json`，检查脚本为 `artifacts/diagnostics/check-money-temperature.py`。

对更新前数据库副本进行实际整机计费得到约 0.070673 度、0.056538 元，覆盖约 52 分钟，仅统计有效记录，没有未计价电量。该副本结果用于验证历史可恢复，不能视为完整日/月费用，见 `artifacts/diagnostics/money-temperature-billing.json`。

旧管理员实例运行期间未强制退出或覆盖其文件；从托盘正常退出后重新打开根入口可加载新版。新版管理员窗口实时交互、实际 Windows 登录、Windows 10、所有 DPI 与完整资源占用仍需实机验收；静态渲染不替代动画观感验收。
