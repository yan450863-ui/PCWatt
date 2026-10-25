# 界面改版验证：暖白纸 + 墨青单强调色

- 日期：2026-10-25
- 范围：视觉令牌与控件样式、窗口外壳、首页信息架构、设置页分区、曲线与品牌图标配色
- 结果：构建零警告零错误，Debug 与 Release 各 219 项测试通过

## 一、设计方向

浅色单主题：暖白纸中性底 + 墨青（`#0F766E`）单一强调色，琥珀色仅用于“估算 / 缺失 / 需要授权”。规范已抽成工作区共用文件 `D:\工作间\精简软件配色`（`配色规范.md` + WPF / CSS / Compose 三份令牌），本项目是参照实现。

`src/PCWatt.App/App.xaml` 集中定义令牌，XAML 只引用名称。

| 用途 | 取值 |
| --- | --- |
| 内容区底色 | `#F4F0E6`（纸） |
| 外壳（标题栏、导航、状态栏） | `#EDE7DC` |
| 卡片 | `#FFFFFF` → `#FCFAF6` |
| 强调卡 | `#EFF8F6` → `#FBFDFC`，描边 `#C7DED9`，数值 `#10312E` |
| 描边 | `#E3DDD2` / 强 `#D2CABA` / 弱 `#ECE7DD` |
| 正文 | `#1F2621` / 次要 `#57605B` / 弱化 `#8A938D` |
| 强调色 | `#0F766E`，悬停 `#0C625C`，浅底 `#E4F0ED`，描边 `#B7D6D0` |
| 估算与提示 | 文字 `#A8620C`，底 `#FBF1E1`，描边 `#E8D3B4`；错误 `#B3261E` |
| 数据系列 | `#0F766E` `#B4690E` `#3F6FB5` `#7A5AA0` `#5B8A72` `#A0566B` |

字号：页面标题 21、卡片标题 14、正文 12、次要 11、徽标 9.5 至 10.5；数字统一 Segoe UI Light 并使用等宽数字（Tribular），电费 34、当前功率 26。圆角：卡片 13、控件 7 至 9、分段轨道 12。

## 二、窗口外壳

- `WindowStyle=None` + `WindowChrome`：`CaptionHeight=46`、`ResizeBorderThickness=6`、`UseAeroCaptionButtons=False`，标题区域由 XAML 自绘（品牌标、`PC Watt`、当前页名、记录状态徽标、最小化/最大化/关闭按钮，按钮通过 `WindowChrome.IsHitTestVisibleInChrome` 可点击）。
- 关闭按钮沿用 `Close()`，即“关闭到托盘”设置仍然生效；最大化字形在 `StateChanged` 中切换 `E922`/`E923`。
- `WindowAppearance.Apply`：标题栏属性按浅色主题设为 0；`33` 为 Win11 圆角（`DWMWCP_ROUND`）；`34` 为窗口描边色 `#E3DDD2`；`ExtendFrameIntoClientArea(1,1,1,1)` 在自绘外壳下恢复系统阴影。
- 底部固定状态栏（记录状态点、记录提示、错误信息、后台频率）与滚动内容分离，长页面滚动时不再带走这条信息。

## 三、信息架构

- 首页：页头（标题、副标题、日期时间、暂停/恢复）→ 部件分段控件 → 设备芯片 → 指标区（电费强调卡 + 所选部件功率卡）→ 曲线卡（型号、平均峰值、三项占用与温度、曲线、更新时间与覆盖率）→ 记录详情（默认收起）。
- 电费卡：今日 / 本月 / 今年共用一张卡，卡内右上为“今日 / 本月 / 今年”分段切换（白底 + 强调色文字标示选中），下方依次为金额、已记录电量、计价说明、覆盖细条与“今日 / 本月 / 今年至今覆盖 x%”。切换只改时间范围：口径、缺失不补算与非重叠计费规则不变。今年区间由 `IWholePcBillingService.GetYearAsync` 单独读取，服务内缓存 20 秒，其余时间沿用普通刷新。
- 设置页：`启动与后台`（自启动、完整硬件、托盘、界面动效四条开关行）、`记录频率`、`通知`、`计费`、`高级`、`关于`；折叠区为紧凑单行标题 + 箭头。
- 控件：分段式部件切换、芯片式设备切换、覆写模板的开关、勾选框、输入框、下拉框、日期选择器（含浅色日历）、滚动条与提示框；浅色主题下 DWM 标题栏、日期弹层、勾选框不再出现深色残留。

## 四、构建与测试

```text
dotnet build PCWatt.sln -c Debug     → 0 警告 0 错误
dotnet build PCWatt.sln -c Release   → 0 警告 0 错误
dotnet test -c Debug   --no-build    → 220 通过
dotnet test -c Release --no-build    → 220 通过
```

新增 `BillingCardSwitchesBetweenDayMonthAndYearWithoutChangingWholePcScope`：切到本月/今年后同一卡片的金额、已记录电量、覆盖文案与选中态随之变化，且本月不触发今年读取、今年只读取一次以上即生效。

`WindowRenderTests` 未修改布局断言，仍要求默认与最小窗口内完整展示曲线；另新增“切换本月/今年后分段控件高亮对应项”的断言，并输出两张切换态截图。

## 五、渲染截图

`artifacts/ui/` 由渲染测试重新生成，均为固定样本或空数据，不写用户数据库：

- 概览：`dashboard.png`、`dashboard-minimum.png`、`hardware-dashboard.png`、`hardware-minimum.png`、`hardware-all-hidden.png`、`hardware-modes.png`、`simple-whole-fixture.png`、`simple-memory-fixture.png`、`simple-storage-fixture.png`、`whole-unknown.png`、`whole-estimated-fixture.png`
- 带数值样本：`tray-utilization-fixture.png`、`tray-utilization-minimum-fixture.png`、`billing-month-fixture.png`、`billing-year-fixture.png`（后两张为同一张卡切到本月 / 今年的状态）
- 设置：`settings.png`、`settings-tariffs.png`、`settings-tariffs-minimum.png`、`settings-report.png`、`settings-easy-fixture.png`、`settings-advanced-fixture.png`

## 六、发布与实机状态

- 当前发布目录 `artifacts\PCWatt-billing-switch-preview`（47 个文件：应用、启动器、许可、LGPL 模块源码、第三方声明），根目录 `启动.cmd` 已指向该目录。上一版 `artifacts\PCWatt-paper-ui-preview` 曾被正在运行的实例占用，未能覆盖写入，退出后可直接删除。
- 窗口外壳链路（WindowChrome + 自绘标题栏 + DWM 属性 + 正常退出）在上一轮发布目录上做过一次实机验证：窗口可见、响应正常、`DwmSetWindowAttribute` 属性 33 返回 `hr=0` 且读回值 2、关闭后按正常流程退出。本轮只改属性取值与配色，未重复实机探测；新版启动仅经构建与 XAML 渲染测试验证。
- 自启动项当前指向上一轮目录的启动器。**双击根目录 `启动.cmd` 运行一次新版即可让自启动目标更新到新目录**；该目录删除前不要先删旧目录，否则自启动会失效直到下次运行。
- 若旧实例仍在托盘运行，新启动会唤回旧窗口；先在托盘菜单“退出”，再双击 `启动.cmd`。

## 七、尚未验收

- 自绘标题栏的拖动、双击最大化、贴边分屏与还原后的内容尺寸。
- 100%、125%、150% 等缩放比例下的排版，以及多显示器跨屏显示。
- 曲线呼吸光点、数值过渡的实机观感，以及窗口隐藏时动效停止。
- 浅色日历弹层在真实点击下的视觉一致性。
