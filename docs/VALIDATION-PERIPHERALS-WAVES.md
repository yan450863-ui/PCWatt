# 外设、电源与波浪图验证

2026-10-28；本地预览版 0.3.0，Windows 11 x64，SDK 10.0.401。

## 已实现的口径

- 首页保持五项：整机、CPU、显卡、外设、其他；内存继续记录并归入主机“其他”，CPU/显卡/内存占用率保留。
- 外设默认关闭，无预设瓦数。可填写显示器及其他独立供电外设的使用功率，保存并启用后从下一次采样加入总功耗及总电费；选择外设时独立显示其功率、电量和历史电价费用。USB 键鼠等已由主机供电，不能重复填写。
- 外设估算不随 Windows 亮屏/熄屏状态自动变化，外设关闭时需关闭估算。状态提示与手动估算明确分开。空白项目不计入，主机不可读时总值保持未知，外设仍能单独查看。
- 启用外设时只记录一条可计费总量，另存主机与外设来源用于核对；不会同时把主机和总量各计一次。开关、参数变更保留旧来源，暂停与缺失不跨越积分，不重算以前的电费。
- 设置保存使用 schema 3，读取兼容原 schema 1/2。SQLite 仍为 schema 5，无表结构迁移。

## 电源遥测

通过现有 LibreHardwareMonitor 电源组尝试读取支持的设备。只接受明确的交流总输入或原生直流总输出；交流输入保留为电源遥测，直流输出按 90% 效率估算交流输入，并明确标记。CPU/GPU 不再叠加到电源总量。额定功率、分路、缺失分路合成总值、多电源歧义、负数及无效读数不能冒充整机输入。无可用接口则回退到原组件估算；不安装驱动或写控制参数。

支持情况取决于具体型号及数据连接，普通电源不能通过额定瓦数获得实时耗电。参考 [Corsair 官方监控说明](https://help.corsair.com/hc/en-us/articles/360060845091-iCUE-How-to-Set-up-Corsair-power-supply-units-in-iCUE-4-or-newer)、[LibreHardwareMonitor Corsair 源码](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/master/LibreHardwareMonitorLib/Hardware/Psu/Corsair/CorsairPsu.cs)、[MSI 源码](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/master/LibreHardwareMonitorLib/Hardware/Psu/Msi/MsiPsu.cs)。

本机普通权限探测没有发现可读电源遥测，仍采用组件估算；原有显卡/温度采集正常。支持电源的分支已通过替身测试，尚未在真实数字电源上验收。诊断在 artifacts/diagnostics/peripheral-psu-live-20261028.json。

## 波浪图

分色波浪面积、总量曲线及少量数据点替代柱图。CPU 蓝、显卡暖橙、外设灰紫、其他青绿；无法核对的主机分解保留灰色。平滑使用有界的共同曲线权重，分层不交叉，不产生负值或峰值过冲；不生成新样本，不改变原积分。完全未知区间断开，悬浮卡仍读取真实统计桶且保持两位小数。

WPF 实际渲染检查覆盖小时、日、周、月、年、历史月、外设、悬浮卡以及外设设置的默认/最小窗口。修复最小窗口底部挤压；设置截图滚动至目标项所在的正确容器。预览均使用测试数据，主图已更新到 docs/images/overview-fixture.png，本地全套截图在 artifacts/ui。

后台仍为同一个采样循环；外设计算与电源读数复用该循环，界面关闭后继续释放图表与动效。本轮未重新测量长时 CPU/内存占用，不声称具体资源降幅。

## 实际执行结果

Debug 和独立目录 Release 构建均零警告、零错误；各 282 项测试通过，0 失败、0 跳过。新增覆盖电源输入/输出优先级、分路与额定功率排除、无效/多电源回退、外设总量不重复、主机未知、历史费用与恢复、开关共用单循环、设置校验和保存、状态提示及波浪边界。

```powershell
dotnet build PCWatt.sln --no-restore
dotnet test PCWatt.sln --no-build --no-restore -- 'RunConfiguration.DotNetHostPath=C:\Program Files\dotnet\dotnet.exe'
dotnet build PCWatt.sln -c Release --artifacts-path artifacts/build-peripherals-waves --no-restore
dotnet test PCWatt.sln -c Release --artifacts-path artifacts/build-peripherals-waves --no-build --no-restore -- 'RunConfiguration.DotNetHostPath=C:\Program Files\dotnet\dotnet.exe'
.\packaging\Build-Release.ps1
.\packaging\Test-Release.ps1
```

更新前备份 artifacts/recovery/peripherals-waves-20261028。最终只读校验保留原 177,239 条样本、两个电价版本、通知和设置，schema 5 / integrity ok；设置字节未变。原监控实例继续运行，未被强制关闭。五种历史范围在备份上核对图表与账单一致、硬件分解守恒；诊断在 artifacts/diagnostics/peripheral-waves-history-audit-20261028.json、peripherals-waves-preservation.json。

## 本地分发

- 安装包：artifacts/releases/0.3.0/PCWatt-0.3.0-Setup-win-x64.exe，57,259,359 字节；SHA256 `3a9675edecd5af9de123dd7485158edc9f2462f1b44f33f2b4fe4b55a3ab6c23`。
- ZIP：PCWatt-0.3.0-win-x64.zip，80,768,165 字节；SHA256 `1e999e5bd709cefb1de4aa2a1e4156056b266dbd2af399fa306eaaff7296e715`。
- 实际静默安装、关键文件核对、屏蔽系统 dotnet 后加载自带 .NET 10.0.12 / SQLite、卸载均通过；版本 0.3.0.0，原设置和自启动命令保持不变。证据在 artifacts/diagnostics/install-smoke-0.3.0。
- 根启动入口指向 artifacts/build-peripherals-waves/bin/PCWatt.App/release/Launch.cmd。旧托盘实例正常退出后再打开新版或安装；旧 0.1.0 / 0.2.0 包保留。

0.3.0 未上传 GitHub，公开下载仍为 0.1.0。安装包未签名，Windows 10、真实数字电源、多屏与所有 DPI 未在本轮全面实机覆盖。
