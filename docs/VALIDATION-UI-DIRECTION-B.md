# 方案 B 界面验证

日期：2026-11-28。界面改版纳入 0.3.3-preview.2；发行构建与安装验证见 VALIDATION-GITHUB-0.3.3-preview.2.md。

## 实现范围

- 复用 WPF、现有主题资源与控件模板；深墨绿侧栏、浅灰工作区、深绿电费卡和白色功率/图表卡，无新增依赖。
- 按用户要求删除页头副标题、标题栏重复页名、设置中的宣传性说明；保留单位、估算来源、缺失记录、权限和备份等必要说明。部件切换仅保留一处。
- 暂停记录使用次级按钮。导航具有实心选中背景、白字、字重与左侧标记；按钮、分段、设置控件具备清晰的状态与键盘焦点。焦点叠层不改变布局尺寸。
- 收紧主卡片与图表头部，删除重复指标行，保持原默认与最小窗口图表高度约束。
- 图表仅调整小字颜色；计费、采样、硬件读取、数据库与图表积分规则未修改。

## 检查结果

Release 构建通过。17 项定向测试通过：`WindowRenderTests`、暂停切换、`SimpleSelectionTests`、`ClearChartTests`。渲染测试覆盖默认、944×660 最小布局、五种周期、部件选择、图表悬停、硬件弹层和设置分区。

增加多显卡最小布局检查：两个设备选择均在选择区边界内且不换行，图表底部完整；同时验证选中分段生成的文字为白色、导航标记实际可见，防止已修复的 WPF 样式继承与布尔触发问题复发。

```powershell
dotnet test tests/PCWatt.Tests/PCWatt.Tests.csproj -c Release --no-restore --artifacts-path artifacts/build-ui-compact --filter "FullyQualifiedName~WindowRenderTests|FullyQualifiedName~DashboardViewModelTests.PauseTogglesStatus|FullyQualifiedName~SimpleSelectionTests|FullyQualifiedName~ClearChartTests" --logger "trx;LogFileName=ui-direction-b.trx" --results-directory artifacts/ui-research-2026-11-27/test-results -- RunConfiguration.DotNetHostPath="C:\Program Files\dotnet\dotnet.exe"
```

默认测试宿主曾因工作区既有进程权限问题中止；显式指定系统 .NET 主机后正常运行，未更改系统权限。最终结果文件位于 `artifacts/ui-research-2026-11-27/test-results/ui-direction-b.trx`。

颜色令牌计算：次要文字对白底 6.45:1、对工作区 5.66:1；电费卡白字 8.42:1；选中导航白字 8.15:1；绿色按钮白字 6.50:1。子代理已查看原图和实际 WPF 渲染截图，确认主要布局无裁切、白字清晰、导航状态生效，多设备最小窗口完整。

## 预览与启动

- [实现截图（测试数据）](../artifacts/ui-research-2026-11-27/pcwatt-direction-b-implemented-2026-11-28.png)
- [轻量预览](../artifacts/ui-research-2026-11-27/pcwatt-direction-b-implemented-2026-11-28.jpg)
- [设计效果图](../artifacts/ui-research-2026-11-27/pcwatt-direction-b-v1-original.png)
- 其他渲染截图：`artifacts/ui/`，包含 `simple-gpu-minimum-fixture.png`。
- 双击根目录 `启动.cmd`，使用已更新的 `artifacts/build-ui-compact/bin/PCWatt.App/release/Launch.cmd`。0.3.3-preview.2 已完成独立发行包构建。

验证为真实 XAML 离屏渲染与 ViewModel 交互；本轮未逐项实测实体窗口鼠标悬停、键盘操作和不同 DPI。设置页的等待硬件读取状态存在重复提示，属于既有状态文案，本轮未修改来源逻辑。

2026-11-28 默认尺寸后续调整：1440×980 → 1120×800，最小 960×700。独立目录 artifacts/build-ui-compact 的 Release 构建和 WindowRenderTests 通过，结果见 test-results/ui-default-size.trx；根启动入口切换至该目录，正在运行的旧实例未关闭。
