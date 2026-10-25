# 图表与显示器状态验证

日期：2026-10-25；Windows 11 x64，.NET SDK 10.0.401。

## 已实现

- 日/月/年和单部件统计沿用同一自然时间轴、有效区间和原始积分。柱宽收窄并增加留白，颜色改为柔和蓝/暖橙/灰紫/青绿，堆叠柱使用圆角外轮廓；浅实线网格、1.6 px 墨青折线，只有最后端点和悬停处有圆点。移除部分覆盖色边，避免被看成另一种硬件组成。未知区间空白且折线断开，真实零值仍可连接。
- 悬浮卡仅保留时间、总耗电及 CPU/显卡/内存/其他耗电；小色点对应图例。保持两位小数，极小正值 < 0.01，不加入覆盖率或缺失长说明。单部件保留平均功率。
- 内存功率卡明确说明通用估算。当前无 DRAM 瓦数时固定采用系统内存合计 8 W，不用负载、温度换算；区间电量还受有效记录时长影响。没有修改任何历史样本、估算常数或计价规则。
- 显示器独立状态采用 `PowerSettingRegisterNotification`、`DEVICE_NOTIFY_CALLBACK` 和 `GUID_SESSION_DISPLAY_STATUS`。原生回调注册后接收当前值及后续变化，没有定时器、隐藏窗口或新依赖，窗口按需销毁不终止订阅；退出注销并保持委托在注册期间存活。无接口或初始化失败显示未知，异常不影响采样。一次读取 WMI 友好型号，不读取序列号或轮询热插拔。
- 图表上方显示亮屏/熄屏/调暗/未知，详情说明会话与实体电源键、多屏的区别。功耗未知显示未计费，不生成零瓦记录、不影响整机估算或账单。检测熄屏会停止装饰动效。后台界面按需释放和唯一采样循环保持原规则。

设计信息参考 [Home Assistant 能源卡](https://www.home-assistant.io/dashboards/energy/) 的耗电及组成展示和 [Tibber 用电查看](https://support.tibber.com/hc/en-us/articles/48143836792849-Understanding-your-energy-consumption) 的周期层次；配色和具体绘制为本项目自编，没有复制素材。实现依据：[PowerSettingRegisterNotification](https://learn.microsoft.com/en-us/windows/win32/api/powersetting/nf-powersetting-powersettingregisternotification)、[显示状态 GUID](https://learn.microsoft.com/en-us/windows/win32/power/power-setting-guids)。

## 实际结果

- Debug 和 Release 的 `dotnet build PCWatt.sln --no-restore` 均零警告、零错误；`dotnet test` 各 **257 通过、0 失败、0 跳过**。记录为 artifacts/test-results/calm-chart-display-{debug,release}.trx。
- 新增 7 项测试验证初始回调、亮/熄/调暗/未知及重复事件、无关或格式异常通知、初始化失败、释放和迟到事件、订阅异常隔离、熄屏停动效但不改变采样及账单。测试使用明确的假原生通知，不实际关闭用户屏幕。
- WPF 实际 XAML 渲染检查三周期默认/最小尺寸、各硬件悬浮卡和内存单项；显示器绑定为亮屏且五项选择保持不变。图片位于 artifacts/ui/stacked-chart-{day,month,year}-fixture.png、clear-chart-hover-{day,month,year}-fixture.png、calm-chart-memory-fixture.png，均为带水印的测试数据，不代表真实测量。
- 普通权限只读探针实际注册成功并收到 G24H2Classics 初始 **On**，Error 为 null、watts 为 null、billed 为 false。记录：artifacts/diagnostics/calm-chart-display-native.json。未请求管理员、未操作熄屏/物理按键；系统熄屏唤醒、多屏、实体按键、Windows 10、多 DPI 和新版原生长时交互尚未实机验收。
- 锁定依赖还原、win-x64 framework-dependent 发布成功。包检查包括 CRC、许可、LGPL 模块源码、SVG/HTML 和无用户数据、无测试文件、无浏览器运行库。

## 交付与数据保留

运行目录 artifacts/PCWatt-calm-chart-display-preview；项目根启动.cmd 和用户启动器目标已更新，HKCU Run 命令保持不变，没有强制关闭旧实例。原实例正常托盘退出后再打开使用新版。

包 artifacts/PCWatt-calm-chart-display-win-x64-framework-dependent.zip：54 文件、4,750,607 字节，SHA256 `4cb3d214d6d7097aa0f8b17ed0ac54cf8857f0c45146da458532b64e9fd5bc6c`。

备份 artifacts/recovery/calm-chart-display-20261025；核对保留 14,649 条原样本、2 个电价版本、通知日期和全部设置；schema 5、数据库完整性 ok。用户现有 10 秒模式、内存视图与管理员偏好未改变，默认新用户仍为一般 30 秒。部署核对见备份内 verified.json。
