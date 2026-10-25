# 托盘与占用率验证

日期：2026-10-25。Windows 11 x64，.NET SDK 10.0.401。

## 已实现行为

- 右下角托盘图标原生悬浮提示显示整机功耗、CPU 温度和显卡温度。固定整机口径，明确“整机估算”，不受主页组件选择影响，不将组件相加冒充实测。显卡优先独显。
- 采样状态事件更新悬浮文本，窗口隐藏后继续工作；鼠标移动到图标时重新检查新鲜度。暂停、休眠、读取异常、未来时间、过期或不可用时不显示旧值。四行文本保持在保守的 63 字符内，不弹出通知或新增定时器，释放托盘时取消采样事件订阅。
- 应用曲线上方显示 CPU、显卡、内存占用百分比与三条简洁进度条，CPU/显卡旁保留温度。CPU/内存是系统范围，显卡跟随所选 GPU；查看其他类别时优先独显。
- CPU 使用 Windows GetSystemTimes 计数差，内核时间扣除其中的空闲部分，显示两次采样间平均；首帧、失败、计数回退或无有效间隔保持未知。超过 64 个逻辑处理器当前不冒充全系统占用。内存使用 GlobalMemoryStatusEx 的物理总量/可用量，两者普通权限可读。显卡只使用 GPU Core 负载，不使用显存、总线或功率百分比。
- 占用与温度同属现有采样帧，后台频率不变；新增数据只驻留内存，不写 SQLite，不进入功率估算或计费。不支持、暂停、过期时显示“暂不可读”，空进度条不表示测得零值。

## 自动化与实读

Debug 与 Release 的 `dotnet build PCWatt.sln --no-restore`、`dotnet test PCWatt.sln --no-build --no-restore`（Release 加 `-c Release`）均成功：各 **219 通过、0 失败、0 跳过**，构建零警告、零错误。锁定依赖还原和 win-x64 framework-dependent 发布通过。

本轮新增 12 项测试，覆盖 CPU 差值/内核空闲/异常基线、物理内存边界、GPU 负载筛选/异常百分比、原硬件会话同读、单循环与暂停释放、设备跟随/新鲜度、托盘整机口径/估算标记、模拟/负数/断线排除、暂停/休眠/失败/过期及文本长度。TRX：`artifacts/test-results/tray-utilization-debug.trx`、`tray-utilization-release.trx`。

实际 XAML 在默认和最小尺寸完成渲染，校验费用、温度、三项占用绑定和完整曲线可见；预览 `artifacts/ui/tray-utilization-fixture.png` 与 `tray-utilization-minimum-fixture.png` 带测试数据水印，不写用户数据库。

普通权限硬件探针两帧读数：CPU 首帧未知、第二帧约 21.75%；物理内存约 38.01%；RTX 5060 Ti 核心 1%，核显核心 0%。结果在 `artifacts/diagnostics/tray-utilization-hardware.json`；这是当时实读，不保证与任务管理器其他口径或时间点一致。

## 发布和保留

当前运行目录 `artifacts/PCWatt-tray-utilization-preview`，发布包 `artifacts/PCWatt-tray-utilization-win-x64-framework-dependent.zip`。包内 47 个文件、必需启动器/许可/LGPL 模块源码、CRC 和无用户数据检查均通过；大小与 SHA256 见 `artifacts/diagnostics/tray-utilization-package.json`。

更新前快照 `artifacts/recovery/tray-utilization-20261025` 保留数据库、设置、根入口、自启动目标和注册项命令。4162 条旧样本、1 个电价版本、1 条通知日期全部保留，schema 5 与数据库完整性正常，设置逐字段不变（包括当前 10 秒模式）。根入口和用户启动目标指向新目录，HKCU Run 命令不变，启动器文件和 ZIP 主 DLL 均与运行目录一致。验证结果为备份目录 `verified.json`；检查使用 `artifacts/diagnostics/check-money-temperature.py verify tray-utilization`。

没有强制退出旧管理员进程或覆盖其运行目录。正常从托盘退出后再次打开根入口加载新版。系统托盘实际悬浮、管理员 CPU 温度、Windows 登录、Windows 10、完整 DPI 和资源占用尚未实机验收；原生 Tooltip 的展示时机由 Windows 管理。此前计费与温度边界见 [电费与温度验证](VALIDATION-MONEY-TEMPERATURE.md)。
