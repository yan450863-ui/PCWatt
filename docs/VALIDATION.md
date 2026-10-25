# 第一阶段验证记录

本文保留第一阶段历史结果。当前第二阶段验证见 [VALIDATION-PHASE2.md](VALIDATION-PHASE2.md)，生命周期和界面已更新。

状态：第一阶段已实现，完整还原、构建与 50 项测试通过。用户已确认后续补充规范分阶段实施。

环境检查（2026-10-24，Asia/Shanghai）：

- 原指定目录不存在，创建新的 `PCWatt` 子目录，未覆盖已有项目。
- Windows 11 专业版，10.0.26300，x64。
- .NET SDK：8.0.401、10.0.401；本项目使用 10.0.401。
- MSBuild 18.9.11；Windows Desktop Runtime 10.0.12；存在 WindowsDesktop reference pack。
- Visual Studio Community 2026 18.10.1；命令行使用 SDK 自带 MSBuild。
- 未发现适用的已有 AGENTS.md 或现存项目。

## 最终执行

```powershell
dotnet restore PCWatt.sln --locked-mode
dotnet build PCWatt.sln
dotnet test PCWatt.sln --logger "trx;LogFileName=phase1.trx" --results-directory artifacts/TestResults
```

- 四个项目依赖还原成功；lock files 可通过 locked-mode 校验。
- 构建：成功，0 warning、0 error。
- 测试：50 passed、0 failed、0 skipped。
- WPF 解析实际 XAML 并输出默认（1064×860 内容区域）和最小尺寸（804×640 内容区域）图片，已目视检查。默认视图包含卡片、点状折线、覆盖率与来源；最小视图通过滚动访问其余内容。
- 电价默认未配置，费用明确显示未知；默认采样 10 秒，支持四档配置。
- 运行时许可与声明配置为复制到构建/发布目录，测试工具不进入 App 运行时依赖。

TRX：`artifacts/TestResults/phase1.trx`。渲染图：`artifacts/ui/dashboard.png`、`artifacts/ui/dashboard-minimum.png`。产物目录被 gitignore 排除，测试可重新生成。

## 已解决的问题

- NuGet 首次还原出现大型包下载 EOF；从同一官方 NuGet 地址分段下载至临时本地缓存后还原成功。使用 `dotnet nuget verify --all` 核对两个包的签名和完整性，随后普通 NuGet 源的 locked-mode 还原成功。没有替换组件或关闭签名验证。
- 首次完整编译发现测试项目启用 WPF 后未隐式引入 System.IO，已显式补齐。
- 首次测试 47/48 通过；渲染测试发现 ProgressBar 默认双向绑定不能写入只读覆盖率属性，已改为 OneWay。后续补充了未配置电价相关测试，最终 50/50 通过。
- 渲染预览背景与默认高度已修正；所有演示图明确标记模拟，布局测试数据不写入用户数据库。

## 验证边界

当前会话没有 computer-use 所需的 node_repl，未进行桌面自动点击验收。使用离线 WPF 渲染与 ViewModel 测试验证布局和暂停/失败/过期显示。

本轮没有注册自启动、托盘、通知、完整设置页、历史电价、五种统计视图或数据管理界面。这些保存在产品规范与路线图中，不能作为已实现功能。

本记录只报告实际验证，不宣称已在 Windows 10、真实传感器或所有显示缩放比例上运行。
