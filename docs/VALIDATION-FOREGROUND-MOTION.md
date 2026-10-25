# 前台动效与按需界面验证

日期：2026-10-25；Windows 11 x64，SDK 10.0.401。

## 当前行为

电费卡增加人民币流光图标，功率卡根据整机、CPU、显卡、内存、其他分类切换能量环、芯片、风扇、内存和连接图形。6 份 SVG 均为自编素材，沿用项目 MIT 许可，无新增依赖。HTML 预览和应用共用 SVG 内容，预览不接硬件或用户数据；应用由轻量 WPF 播放器解析内置形状及 opacity、stroke-dashoffset、rotate 动画，不嵌入浏览器或执行 HTML 脚本。

动画最高 24 帧/秒。只有窗口可见、未最小化、处于前台、概览可见、记录运行、动效及系统客户端动画均允许时播放。停止时移除 WPF 动画时钟；关闭时解除系统参数和采样事件订阅，释放动画图形，不注册 CompositionTarget.Rendering 或额外计时循环。数值与动画不参与积分、存储或计费。

后台启动不创建主窗口、ViewModel、图表或主题。关闭到托盘时销毁界面及主题字典，保留采样、批量保存、偏好、单实例与托盘。点开托盘再创建窗口和界面统计服务，持久化选择仍恢复。最小化会取消查询、停止动效与界面时钟；失焦但仍可见时保留静态数据。通知失败等状态在界面未创建时仅保存简短状态，打开后显示；全退出仍保存并释放硬件。

这属于释放界面对象，不代表卸载 .NET 或硬件库。没有强制 GC、裁剪工作集或声称从用户观察的约 100 MB 降到某个值；新版实际工作集、私有内存及长期资源曲线尚未实测。

## 实际验证

- Debug、Release 构建均零警告零错误，测试各 **250 通过、0 失败、0 跳过**。锁定还原及 win-x64 framework-dependent 发布通过。结果：artifacts/test-results/on-demand-motion-{debug,release}.trx。
- 新增测试逐一检查 6 份 SVG 的动画轨道：每份连续启停 20 次，停止后活动轨道为 0，重复播放不叠加时钟。另检查失去前台时关闭动效、保留读数及监控状态。
- 窗口生命周期也控制由切换按钮发起、没有显式调用方令牌的统计查询；新增测试确认关闭时同样取消，不重新填充界面缓存。
- 实际窗口生命周期集成测试检查未打开时创建次数为 0、同一窗口复用、关闭后 Content/DataContext/主题为空；连续 3 次重新创建后窗口数为 0。测试监控仍按 5 秒记录共 4 条，只有一个调度计时器，未提前释放硬件。该测试使用独立内存记录，不改用户数据。
- 实际 XAML 在默认与最小尺寸渲染成功，SVG 与中文读数不遮挡图表，保留精简硬件悬浮卡。预览为 artifacts/ui/foreground-motion-whole-fixture.png 与 foreground-motion-gpu-fixture.png，带测试水印，非真实测量。
- HTML 的 SVG 内容一致性、前台/失焦/隐藏/减少动态效果/手动暂停/类别切换通过 Node 源码状态测试，结果 artifacts/diagnostics/motion-preview-source-check.json。没有网络或轮询。内置浏览器的本地 file 协议安全策略拒绝打开 HTML，未绕过策略；HTML 浏览器视觉验收未完成，WPF 渲染与动画轨道已独立验证。Windows 原生焦点/托盘长时交互、多 DPI、Windows 10 与不同硬件仍需实机验收。

实现依据：[WPF 动画概述](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/graphics-multimedia/animation-overview)、[SVG animateTransform](https://developer.mozilla.org/en-US/docs/Web/SVG/Reference/Element/animateTransform)。本地 HTML 为 src/PCWatt.App/Assets/Motion/index.html；主题为 Themes/Theme.xaml。

## 发布与保留

运行目录 artifacts/PCWatt-on-demand-motion-preview；根启动入口和用户启动器目标已更新，注册项命令不变，未退出或覆盖旧管理员实例。需要正常托盘退出旧版后重新打开。

包 artifacts/PCWatt-on-demand-motion-win-x64-framework-dependent.zip，54 文件、4745763 字节，SHA256 `3dae2923fa16c4e37cbacaa1a3a1b1be66dddb5d0e7b938d7a48900c1addf036`。6 个 SVG 与 HTML、完整许可、LGPL 模块源码、CRC、不含用户数据及不含浏览器运行库检查通过。

备份 artifacts/recovery/on-demand-motion-20261025；13,681 条原样本、2 个电价版本、通知日期与全部用户设置保留，schema 5、数据库完整性正常。用户当前 10 秒模式、管理员偏好和视图设置均不变，部署核对见备份内 verified.json。
