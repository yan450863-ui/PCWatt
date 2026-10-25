# 第二阶段与界面改版验证

2026-10-24，Asia/Shanghai。用户要求改善点状图、增加动态并继续下一步。

## 实现结果

- 深色仪表盘、侧边导航、渐变功耗卡片、柔和面积曲线与暗色滚动条。
- 功耗数值 650 ms 过渡、新样本 500 ms 尾段过渡、末端呼吸光点及悬停时间/功率读数；有动效开关，并遵循系统客户端动画设置。隐藏、最小化或切出概览时不应继续不必要绘图，动画不写入测量或积分。
- 每用户单实例：命名互斥量、只允许当前用户访问的本地管道；第二次启动请求唤回主窗口。
- 用户级 Run 注册项，带引号的 exe 路径与 `--background` 参数；开关关闭立即移除该项，不退出当前进程。
- 托盘打开、最近模拟读数、暂停/恢复、设置和退出；关闭主窗口默认隐藏到托盘。托盘创建失败时关闭窗口执行完整退出。
- 唯一应用级采样循环，默认 10 秒，支持 2/5/10/30 秒；窗口仅保留可见时的时钟刷新，不驱动采样。
- 缓冲数据每 30 秒或达到 6 条时按 SQLite 事务写入，暂停和正常退出强制 flush；失败保留待写批次供重试。后台仅执行采样和必要写入，不查询/绘制主窗口统计。
- 暂停、恢复、进程重启、休眠、电源恢复、时钟跳变均建立不连续区间；时钟回退期间不把新样本插入已记录的历史时间。
- SQLite schema 1→2 事务迁移，保留旧样本；新样本存储 `MaximumIntervalSeconds`。切换频率不会把旧缺失区间重新解释成有效耗电。
- 原子保存带版本的用户设置，保留旧配置文件。并发设置更改串行计算，互不覆盖；修改动效/自启动不会重置采样等待。

## 实际执行

环境：Windows 11 x64、.NET SDK 10.0.401、SDK 自带 MSBuild。

```powershell
dotnet build PCWatt.sln
dotnet test PCWatt.sln --logger "trx;LogFileName=phase2.trx" --results-directory artifacts/TestResults
dotnet restore PCWatt.sln --locked-mode
dotnet publish src/PCWatt.App/PCWatt.App.csproj -c Release --no-restore --self-contained false --output artifacts/PCWatt-preview
dotnet test PCWatt.sln -c Release --no-restore --logger "trx;LogFileName=phase2-release.trx" --results-directory artifacts/TestResults
```

构建成功：0 warning、0 error。测试：**75 passed、0 failed、0 skipped**。测试包括原有积分/SQLite/配置/ViewModel 边界场景，以及批量写入、schema 迁移、保存历史间隔策略、单实例 IPC、设置恢复/并发与失败回滚、频率切换、取消读取、暂停/休眠边界、失败重试和退出保存。

Release 发布成功，Release 配置另有 75/75 通过。预览目录 `artifacts/PCWatt-preview/` 包含 exe、依赖、许可和使用说明；需要已安装的 .NET 10 Windows Desktop Runtime。压缩包为 `artifacts/PCWatt-preview-win-x64-framework-dependent.zip`，不包含用户数据库或设置。尚未在生产用户目录运行此预览。

测试结果：`artifacts/TestResults/phase2.trx`。

实际 XAML 在 STA 线程解析并渲染，已目视检查默认尺寸概览、最小尺寸概览和设置页：

- `artifacts/ui/dashboard.png`（1164×810 内容区域）
- `artifacts/ui/dashboard-minimum.png`（944×660 内容区域）
- `artifacts/ui/settings.png`（1164×810 内容区域）

布局样本由测试替身生成，仅渲染至本地测试产物，不写入用户数据库。

## 修正与边界

初次测试发现旧断言仍预期原间隔默认值，并修正了休眠测试对“恢复后第一条样本”的并发时序假设；最终方案保留 30 秒基础阈值，并把变化后的阈值随新样本持久化。所有上述结果均针对最终代码。

自动化使用临时 SQLite、临时设置文件、内存自启动存储和独立命名对象，未修改当前机器实际自启动项。没有启动生产 App 来写入真实用户目录。

未进行 Windows 登录自启动、实际托盘点击、真实休眠/关机、Windows 10、多显示器缩放或动画帧率/CPU/内存实机验收。当前会话不具备 desktop computer-use 的 node_repl 工具；静态渲染验证不等于已验证实时动画观感。

未接入真实硬件、通知系统、历史电价、五种历史统计、CSV/JSON 导出或备份恢复界面。它们继续按后续阶段实施。
