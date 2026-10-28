# 历史统计与传感器扩展验证

2026-10-28；本地预览版 0.2.0，Windows 11 x64，.NET SDK 10.0.401 / Runtime 10.0.12。

## 本轮功能

- 小时：默认最近 60 分钟，按分钟积分；也可选择历史日期及具体小时。
- 日：所选本地日期，按小时；周：所选日期及前六天，按天；月：自然月，按天；年：自然年，按月。
- 日期栏支持前后时段、选择日期、回到现在；未来时段不可访问，当前周期只计算截止当前的实际记录。电费卡和图表使用同一查询范围，当前功率、温度和占用仍为实时数据。
- 查询范围与观测截止时间分离：旧日期保留右侧已知样本用于边界裁切，长缺失不插值、重复来源时间戳不重复累计，账单继续使用历史生效电价。
- 本地自然日按真实 UTC 间隔积分，夏令时日期保留 23/25 小时。小时前后导航逐一访问两个重复小时；手动指定重复小时选择后一个，跳过的小时提示不可选，悬停在歧义时间显示 UTC 偏移。
- 更多传感器默认收起，展示现有接口中的 CPU/显卡附加温度、硬盘温度和风扇 RPM；逐项区分授权、不支持、读失败、过期和暂停。温度/转速不估算、不持久化、不进入电费。
- 复用原硬件更新与唯一采样循环，无新驱动、额外硬件轮询或后台界面。失焦/隐藏关闭面板，窗口仍按需释放。
- 全年硬件拆分每部件只读一次完整查询窗口，再按桶裁切，减少重复数据库读取。

## 实际构建与测试

Debug 构建零警告、零错误；273 项测试通过，0 失败、0 跳过。Release 在独立目录构建，同样零警告、零错误和 273 项测试通过。

```powershell
dotnet build PCWatt.sln --no-restore
dotnet test PCWatt.sln --no-build --no-restore --logger 'console;verbosity=minimal' -- 'RunConfiguration.DotNetHostPath=C:\Program Files\dotnet\dotnet.exe'
dotnet build PCWatt.sln -c Release --artifacts-path artifacts/build-history-sensors -p:RestoreLockedMode=true
dotnet test PCWatt.sln -c Release --artifacts-path artifacts/build-history-sensors --no-build --no-restore --logger 'console;verbosity=minimal' -- 'RunConfiguration.DotNetHostPath=C:\Program Files\dotnet\dotnet.exe'
```

原 Release 输出目录被仍在运行的旧实例 dotnet.exe（PID 20380）锁定，普通 Release 构建报文件复制失败。改用独立构建目录后通过，未强制终止旧实例或修改安全权限。启动器复制改为获取子项目的实际输出路径，兼容独立构建和发布目录。

新测试覆盖五周期粒度、历史日/闰月、跨午夜边界、旧电价与重开数据库、重复/负数/空值/长缺失、未来时段、夏令时 23/25 小时和重复小时导航、慢查询结果丢弃、传感器无效值和零 RPM、暂停/过期状态，以及同一个采样循环、不持久化温度/转速。

XAML 实际渲染覆盖原有日/月/年、小时及最小窗口、周、历史月和传感器面板。检查图表高度、底部可见、所选周期和电费/图表电量一致；渲染文件位于 artifacts/ui/history-*-fixture.png 和 expanded-sensors-fixture.png。均为隔离的测试数据，没有写入真实数据库。

## 真实数据和本机接口

- 更新前备份至 artifacts/recovery/history-sensors-20261028，含 160,589 条样本、2 个电价版本、通知记录和设置；最终只读校验原样本、电价、通知均保留，设置字节不变，schema 5 / integrity ok。数据库没有新增迁移。
- 在备份上核对五种历史范围：图表与账单有效时长一致，电量差小于 10⁻¹² kWh，各桶硬件拆分总量守恒。一次本机查询用时约小时 63 ms、日 364 ms、周 361 ms、月 556 ms、年 679 ms，属于当前快照测试，不代表所有设备性能。
- 普通权限实读 RTX 5060 Ti 核心温度约 32.2 °C、显存结温 42 °C，两路显卡风扇为 0 RPM；核显也有附加温度读数。CPU 温度提示需要授权，当前 Ti600 SSD 温度接口不可读。不保证每台硬盘、风扇或 GPU 有这些接口；没有新安装驱动，管理员下的全部附加读数尚未逐项实机验收。
- 诊断仅本地保存：artifacts/diagnostics/history-sensors-live-20261028.json、history-ranges-audit-20261028.json、history-sensors-data-preservation.json。

## 本地安装包

使用 packaging/Build-Release.ps1 构建自包含安装包和 ZIP，输出到 artifacts/releases/0.2.0，保留原 0.1.0。安装包包含 .NET Desktop 10.0.12、原生 SQLite、可替换的硬件 DLL、第三方许可及相关源码。

- PCWatt-0.2.0-Setup-win-x64.exe：57,231,867 字节；SHA256 `789fcae4ba836497f5da0ccb8901488b01452f76ab031804076243458a1ea4dc`。
- PCWatt-0.2.0-win-x64.zip：80,759,164 字节；SHA256 `ce871752c888c399f37a60b60d4cba1c3c9ea772af4f9af9a31e47da9b632c57`。
- packaging/Test-Release.ps1 已实际安装、检查文件、屏蔽系统 dotnet 后加载应用内运行时/SQLite、卸载，并确认原设置与自启动配置未变。应用报告版本 0.2.0.0，未触及用户数据。证据在 artifacts/diagnostics/install-smoke-0.2.0。
- 根启动入口指向 artifacts/build-history-sensors/bin/PCWatt.App/release/Launch.cmd，其应用 DLL 已通过 --package-check；没有为验收启动第二个监控实例。打开新版前需正常退出旧托盘实例。

0.2.0 尚未上传 GitHub，公开下载仍为 0.1.0。没有执行新版本联网发布。安装包仍未签名；Windows 10、多显示器、全部缩放比例、真实登录/休眠及长时资源曲线未在本轮全面实机验收。
