# 精简硬件悬浮卡验证

日期：2026-10-25；.NET SDK 10.0.401，Windows 11 x64。

整机悬浮卡仅保留时间范围、总耗电及 CPU、显卡、内存、其他各自耗电；移除覆盖率、缺失段数、长说明和重复的各部件 W 数值。整机估算或模拟数据保留短标记，不能冒充实测。单部件视图没有组成清单时显示区间平均功率。无法核对的组成仍显示未拆分，不猜测比例。

电量统一两位小数。极小正值（不足 0.005 度）显示 < 0.01 度，真实零为 0.00，未知仍为未知。电价输入与计价精度保留，原始样本、积分、账单计算精度不变。小量级纵轴使用标明的瓦时（Wh），刻度同样两位小数。悬浮卡宽度 284 逻辑像素。

Debug 和 Release 的 dotnet build / dotnet test 均成功：各 247 通过，0 失败、0 跳过，零构建警告和错误。锁定还原与 win-x64 发布通过。现有测试检查硬件分类、两位小数、极小正值、真实零与未知，以及不显示覆盖和缺失内容。实际 XAML 三周期悬浮卡渲染位于 artifacts/ui/clear-chart-hover-{day,month,year}-fixture.png，仅为带水印测试数据；新版原生交互和各 DPI 尚未逐项实机验收。

当前入口调用 artifacts/PCWatt-hardware-hover-preview/Launch.cmd，用户启动器目标同步更新，自启动注册项未变。发布包 artifacts/PCWatt-hardware-hover-win-x64-framework-dependent.zip，共 47 文件、4728504 字节，SHA256 `f31b9d8bd0753b1064e94604821f212cd06b8ef1cb919e235c22ca0d3d834753`；许可、CRC、不含用户数据检查通过。

备份 artifacts/recovery/hardware-hover-20261025；12,174 条旧样本、2 个电价版本、通知日期和全部用户设置保留，schema 5、数据库完整性正常。未强制退出或覆盖正在运行的实例；正常从托盘退出后重开根目录入口加载新版。验证结果为备份内 verified.json，测试结果为 artifacts/test-results/hardware-hover-{debug,release}.trx。
